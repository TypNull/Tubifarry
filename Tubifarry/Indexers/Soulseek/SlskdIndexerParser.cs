using FuzzySharp;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Queue;
using System.Text.Json;
using Tubifarry.Core.Model;
using Tubifarry.Core.Telemetry;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Indexers.Soulseek
{
    public class SlskdIndexerParser : IParseIndexerResponse, IHandle<AlbumGrabbedEvent>, IHandle<ApplicationShutdownRequested>
    {
        private readonly SlskdIndexer _indexer;
        private readonly Logger _logger;
        private readonly Lazy<IIndexerFactory> _indexerFactory;
        private readonly IHttpClient _httpClient;
        private readonly ISlskdItemsParser _itemsParser;
        private readonly IHistoryService _historyService;
        private readonly IDownloadHistoryService _downloadHistoryService;
        private readonly IQueueService _queueService;
        private readonly ISentryHelper _sentry;

        private static readonly Dictionary<int, string> _interactiveResults = [];
        private static readonly Dictionary<string, (HashSet<string> IgnoredUsers, long LastFileSize)> _ignoreListCache = new();
        private readonly object _rateLimitLock = new();
        private DateTime _rateLimitCacheTimestamp = DateTime.MinValue;
        private HashSet<string> _rateLimitedUsersCache = new(StringComparer.OrdinalIgnoreCase);

        private SlskdSettings Settings => _indexer.Settings;

        public SlskdIndexerParser(SlskdIndexer indexer, Lazy<IIndexerFactory> indexerFactory, IHttpClient httpClient, ISlskdItemsParser itemsParser, IHistoryService historyService, IDownloadHistoryService downloadHistoryService, IQueueService queueService, ISentryHelper sentry)
        {
            _indexer = indexer;
            _indexerFactory = indexerFactory;
            _logger = NzbDroneLogger.GetLogger(this);
            _httpClient = httpClient;
            _itemsParser = itemsParser;
            _historyService = historyService;
            _downloadHistoryService = downloadHistoryService;
            _queueService = queueService;
            _sentry = sentry;
        }

        public IList<ReleaseInfo> ParseResponse(IndexerResponse indexerResponse)
        {
            List<AlbumData> albumDatas = [];
            try
            {
                SlskdSearchResponse? searchResponse = JsonSerializer.Deserialize<SlskdSearchResponse>(indexerResponse.Content, IndexerParserHelper.StandardJsonOptions);

                if (searchResponse == null)
                {
                    _logger.Error("Failed to deserialize slskd search response.");
                    return [];
                }

                SlskdSearchData searchTextData = SlskdSearchData.FromJson(indexerResponse.HttpRequest.ContentSummary);
                HashSet<string>? ignoredUsers = GetIgnoredUsers(Settings.IgnoreListPath);
                HashSet<string> rateLimitedUsers = GetRateLimitedUsers();

                foreach (SlskdFolderData response in searchResponse.Responses)
                {
                    if (ignoredUsers?.Contains(response.Username) == true || rateLimitedUsers.Contains(response.Username))
                        continue;

                    IEnumerable<SlskdFileData> filteredFiles = SlskdFileData.GetFilteredFiles(response.Files, Settings.OnlyAudioFiles, Settings.IncludeFileExtensions);

                    List<IGrouping<string, SlskdFileData>> directoryGroups = SlskdTextProcessor.MergeDiscSubdirectories(
                        filteredFiles.GroupBy(f => SlskdTextProcessor.GetDirectoryFromFilename(f.Filename)));

                    foreach (IGrouping<string, SlskdFileData> directoryGroup in directoryGroups)
                    {
                        if (string.IsNullOrEmpty(directoryGroup.Key))
                            continue;

                        SlskdFolderData folderData = _itemsParser.ParseFolderName(directoryGroup.Key) with
                        {
                            Username = response.Username,
                            HasFreeUploadSlot = response.HasFreeUploadSlot,
                            UploadSpeed = response.UploadSpeed,
                            LockedFileCount = response.LockedFileCount,
                            LockedFiles = response.LockedFiles,
                            QueueLength = response.QueueLength,
                            Token = response.Token,
                            FileCount = response.FileCount
                        };

                        IGrouping<string, SlskdFileData> finalGroup = directoryGroup;
                        if (searchTextData.ExpandDirectory)
                        {
                            IGrouping<string, SlskdFileData>? expandedGroup = TryExpandDirectory(searchTextData, directoryGroup, folderData);
                            if (expandedGroup != null)
                                finalGroup = expandedGroup;
                        }

                        if (searchTextData.MinimumFiles > 0 || searchTextData.MaximumFiles.HasValue)
                        {
                            bool filterActive = (TrackCountFilterType)Settings.TrackCountFilter != TrackCountFilterType.Disabled;
                            int fileCount = filterActive
                                ? finalGroup.Count(f => AudioFormatHelper.GetAudioCodecFromExtension(f.Extension ?? Path.GetExtension(f.Filename) ?? "") != AudioFormat.Unknown)
                                : finalGroup.Count();

                            if (fileCount < searchTextData.MinimumFiles)
                            {
                                _logger.Trace($"Filtered (too few): {directoryGroup.Key} ({fileCount}/{searchTextData.MinimumFiles} {(filterActive ? "audio tracks" : "files")})");
                                continue;
                            }

                            if (searchTextData.MaximumFiles.HasValue && fileCount > searchTextData.MaximumFiles.Value)
                            {
                                _logger.Trace($"Filtered (too many): {directoryGroup.Key} ({fileCount}/{searchTextData.MaximumFiles} {(filterActive ? "audio tracks" : "files")})");
                                continue;
                            }
                        }

                        AlbumData albumData = _itemsParser.CreateAlbumData(searchResponse.Id, finalGroup, searchTextData, folderData, Settings, searchTextData.MinimumFiles);
                        albumDatas.Add(albumData);
                    }
                }

                _sentry.UpdateSearchResultCount(searchResponse.Id, albumDatas.Count);

                RemoveSearch(searchResponse.Id, albumDatas.Count != 0 && searchTextData.Interactive);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to parse Slskd search response.");
            }

            return albumDatas.OrderByDescending(x => x.Priotity).Select(a => a.ToReleaseInfo()).ToList();
        }

        private IGrouping<string, SlskdFileData>? TryExpandDirectory(SlskdSearchData searchTextData, IGrouping<string, SlskdFileData> directoryGroup, SlskdFolderData folderData)
        {
            if (string.IsNullOrEmpty(searchTextData.Artist) || string.IsNullOrEmpty(searchTextData.Album))
                return null;

            bool artistMatch = Fuzz.PartialRatio(folderData.Artist, searchTextData.Artist) > 85;
            bool albumMatch = Fuzz.PartialRatio(folderData.Album, searchTextData.Album) > 85;

            if (!artistMatch || !albumMatch)
                return null;

            SlskdFileData? originalTrack = directoryGroup.FirstOrDefault(x => AudioFormatHelper.GetAudioCodecFromExtension(x.Extension?.ToLowerInvariant() ?? Path.GetExtension(x.Filename) ?? "") != AudioFormat.Unknown);

            if (originalTrack == null)
                return null;

            _logger.Trace($"Expanding directory for: {folderData.Username}:{directoryGroup.Key}");

            SlskdRequestGenerator? requestGenerator = _indexer.GetExtendedRequestGenerator() as SlskdRequestGenerator;
            IGrouping<string, SlskdFileData>? expandedGroup = requestGenerator?.ExpandDirectory(folderData.Username, directoryGroup.Key, originalTrack).GetAwaiter().GetResult();

            if (expandedGroup != null)
            {
                _logger.Debug($"Successfully expanded directory to {expandedGroup.Count()} files");
                return expandedGroup;
            }
            else
            {
                _logger.Warn($"Failed to expand directory for {folderData.Username}:{directoryGroup.Key}");
            }
            return null;
        }

        public void RemoveSearch(string searchId, bool delay = false)
        {
            Task.Run(async () =>
            {
                try
                {
                    if (delay)
                    {
                        _interactiveResults.TryGetValue(_indexer.Definition.Id, out string? staleId);
                        _interactiveResults[_indexer.Definition.Id] = searchId;
                        if (staleId != null)
                            searchId = staleId;
                        else return;
                    }
                    await ExecuteRemovalAsync(Settings, searchId);
                }
                catch (HttpException ex)
                {
                    _logger.Error(ex, $"Failed to remove slskd search with ID: {searchId}");
                }
            });
        }

        public void Handle(AlbumGrabbedEvent message)
        {
            if (!_interactiveResults.TryGetValue(message.Album.Release.IndexerId, out string? selectedId) || !message.Album.Release.InfoUrl.EndsWith(selectedId))
                return;
            ExecuteRemovalAsync((SlskdSettings)_indexerFactory.Value.Get(message.Album.Release.IndexerId).Settings, selectedId).GetAwaiter().GetResult();
            _interactiveResults.Remove(message.Album.Release.IndexerId);
        }

        public void Handle(ApplicationShutdownRequested message)
        {
            foreach (int indexerId in _interactiveResults.Keys.ToList())
            {
                if (_interactiveResults.TryGetValue(indexerId, out string? selectedId))
                {
                    ExecuteRemovalAsync((SlskdSettings)_indexerFactory.Value.Get(indexerId).Settings, selectedId).GetAwaiter().GetResult();
                    _interactiveResults.Remove(indexerId);
                }
            }
        }

        public static void InvalidIgnoreCache(string path) => _ignoreListCache.Remove(path);

        private HashSet<string> GetRateLimitedUsers()
        {
            if (Settings.MaxGrabsPerUser <= 0 && Settings.MaxQueuedPerUser <= 0)
                return [];

            lock (_rateLimitLock)
            {
                if (DateTime.UtcNow - _rateLimitCacheTimestamp < TimeSpan.FromSeconds(15))
                    return _rateLimitedUsersCache;

                HashSet<string> blocked = new(StringComparer.OrdinalIgnoreCase);

                if (Settings.MaxGrabsPerUser > 0)
                    foreach ((string? user, int count) in GetGrabCounts())
                        if (count >= Settings.MaxGrabsPerUser)
                            blocked.Add(user);

                if (Settings.MaxQueuedPerUser > 0)
                    foreach ((string? user, int count) in GetQueuedCounts())
                        if (count >= Settings.MaxQueuedPerUser)
                            blocked.Add(user);

                _rateLimitedUsersCache = blocked;
                _rateLimitCacheTimestamp = DateTime.UtcNow;
                return blocked;
            }
        }

        private Dictionary<string, int> GetGrabCounts()
        {
            DateTime since = (GrabLimitIntervalType)Settings.GrabLimitInterval switch
            {
                GrabLimitIntervalType.Hour => DateTime.UtcNow.AddHours(-1),
                GrabLimitIntervalType.Week => DateTime.UtcNow.AddDays(-7),
                _ => DateTime.UtcNow.Date
            };

            int? indexerId = _indexer.Definition?.Id;
            Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);

            IEnumerable<string> downloadIds = _historyService.Since(since, EntityHistoryEventType.Grabbed)
                .Select(h => h.DownloadId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (string downloadId in downloadIds)
            {
                DownloadHistory? grab = _downloadHistoryService.GetLatestGrab(downloadId);
                if (grab == null)
                    continue;

                if (!string.Equals(grab.Protocol, nameof(SoulseekDownloadProtocol), StringComparison.OrdinalIgnoreCase))
                    continue;

                if (indexerId.HasValue && grab.IndexerId != indexerId.Value)
                    continue;

                string? username = ExtractUsernameFromUrl(grab.Release?.DownloadUrl);
                if (username != null)
                    counts[username] = counts.GetValueOrDefault(username) + 1;
            }

            return counts;
        }

        private Dictionary<string, int> GetQueuedCounts()
        {
            string? indexerName = _indexer.Definition?.Name;
            Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

            foreach (Queue item in _queueService.GetQueue())
            {
                if (!string.Equals(item.Protocol, nameof(SoulseekDownloadProtocol), StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrWhiteSpace(indexerName) && !string.Equals(item.Indexer, indexerName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.IsNullOrWhiteSpace(item.DownloadId) || !seen.Add(item.DownloadId))
                    continue;

                DownloadHistory? grab = _downloadHistoryService.GetLatestGrab(item.DownloadId);
                if (grab == null)
                    continue;

                string? username = ExtractUsernameFromUrl(grab.Release?.DownloadUrl);
                if (username != null)
                    counts[username] = counts.GetValueOrDefault(username) + 1;
            }

            return counts;
        }

        private static string? ExtractUsernameFromUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;

            int lastSlash = url.LastIndexOf('/');
            if (lastSlash < 0 || lastSlash >= url.Length - 1)
                return null;

            return Uri.UnescapeDataString(url[(lastSlash + 1)..]);
        }

        private async Task ExecuteRemovalAsync(SlskdSettings settings, string searchId)
        {
            try
            {
                HttpRequest request = new HttpRequestBuilder($"{settings.BaseUrl}/api/v0/searches/{searchId}")
                    .SetHeader("X-API-KEY", settings.ApiKey)
                    .Build();
                request.Method = HttpMethod.Delete;
                await _httpClient.ExecuteAsync(request);
            }
            catch (HttpException ex)
            {
                _logger.Error(ex, $"Failed to remove slskd search with ID: {searchId}");
            }
        }

        private HashSet<string>? GetIgnoredUsers(string? ignoreListPath)
        {
            if (string.IsNullOrWhiteSpace(ignoreListPath) || !File.Exists(ignoreListPath))
                return null;

            try
            {
                FileInfo fileInfo = new(ignoreListPath);
                long fileSize = fileInfo.Length;

                if (_ignoreListCache.TryGetValue(ignoreListPath, out (HashSet<string> IgnoredUsers, long LastFileSize) cached) && cached.LastFileSize == fileSize)
                {
                    _logger.Trace($"Using cached ignore list from: {ignoreListPath} with {cached.IgnoredUsers.Count} users");
                    return cached.IgnoredUsers;
                }
                HashSet<string> ignoredUsers = SlskdTextProcessor.ParseListContent(File.ReadAllText(ignoreListPath));
                _ignoreListCache[ignoreListPath] = (ignoredUsers, fileSize);
                _logger.Trace($"Loaded ignore list with {ignoredUsers.Count} users from: {ignoreListPath}");
                return ignoredUsers;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, $"Failed to load ignore list from: {ignoreListPath}");
                return null;
            }
        }
    }
}