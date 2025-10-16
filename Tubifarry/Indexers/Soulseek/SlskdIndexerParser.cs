using FuzzySharp;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using System.Text.Json;
using Tubifarry.Core.Model;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Indexers.Soulseek
{
    public class SlskdIndexerParser : IParseIndexerResponse, IHandle<AlbumGrabbedEvent>, IHandle<ApplicationShutdownRequested>
    {
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
        private readonly SlskdIndexer _indexer;
        private readonly Logger _logger;
        private readonly Lazy<IIndexerFactory> _indexerFactory;
        private readonly IHttpClient _httpClient;

        private static readonly Dictionary<int, string> _interactiveResults = [];
        private static readonly Dictionary<string, (HashSet<string> IgnoredUsers, long LastFileSize)> _ignoreListCache = new();

        private SlskdSettings Settings => _indexer.Settings;

        public SlskdIndexerParser(SlskdIndexer indexer, Lazy<IIndexerFactory> indexerFactory, IHttpClient httpClient)
        {
            _indexer = indexer;
            _indexerFactory = indexerFactory;
            _logger = NzbDroneLogger.GetLogger(this);
            _httpClient = httpClient;
        }

        public IList<ReleaseInfo> ParseResponse(IndexerResponse indexerResponse)
        {
            List<AlbumData> albumDatas = [];
            try
            {
                SlskdSearchResponse? searchResponse = JsonSerializer.Deserialize<SlskdSearchResponse>(indexerResponse.Content, _jsonOptions);

                if (searchResponse == null)
                {
                    _logger.Error("Failed to deserialize slskd search response.");
                    return [];
                }

                SlskdSearchData searchTextData = SlskdSearchData.FromJson(indexerResponse.HttpRequest.ContentSummary);
                HashSet<string>? ignoredUsers = GetIgnoredUsers(Settings.IgnoreListPath);

                foreach (SlskdFolderData response in searchResponse.Responses)
                {
                    if (response.FileCount < searchTextData.MinimumFiles || ignoredUsers?.Contains(response.Username) == true)
                        continue;

                    IEnumerable<SlskdFileData> filteredFiles = SlskdFileData.GetFilteredFiles(response.Files, Settings.OnlyAudioFiles, Settings.IncludeFileExtensions);

                    foreach (IGrouping<string, SlskdFileData> directoryGroup in filteredFiles.GroupBy(f => SlskdTextProcessor.GetDirectoryFromFilename(f.Filename)))
                    {
                        if (string.IsNullOrEmpty(directoryGroup.Key))
                            continue;

                        SlskdFolderData folderData = SlskdItemsParser.ParseFolderName(directoryGroup.Key) with
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

                        if (searchTextData.ExpandDirectory && ShouldExpandDirectory(albumDatas, searchResponse, searchTextData, directoryGroup, folderData))
                            continue;

                        AlbumData originalAlbumData = SlskdItemsParser.CreateAlbumData(searchResponse.Id, directoryGroup, searchTextData, folderData, Settings);
                        albumDatas.Add(originalAlbumData);
                    }
                }

                RemoveSearch(searchResponse.Id, albumDatas.Count != 0 && searchTextData.Interactive);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to parse Slskd search response.");
            }

            return albumDatas.OrderByDescending(x => x.Priotity).Select(a => a.ToReleaseInfo()).ToList();
        }

        private bool ShouldExpandDirectory(List<AlbumData> albumDatas, SlskdSearchResponse searchResponse, SlskdSearchData searchTextData, IGrouping<string, SlskdFileData> directoryGroup, SlskdFolderData folderData)
        {
            if (string.IsNullOrEmpty(searchTextData.Artist) || string.IsNullOrEmpty(searchTextData.Album))
                return false;

            bool artistMatch = Fuzz.PartialRatio(folderData.Artist, searchTextData.Artist) > 85;
            bool albumMatch = Fuzz.PartialRatio(folderData.Album, searchTextData.Album) > 85;

            if (!artistMatch || !albumMatch)
                return false;

            SlskdFileData? originalTrack = directoryGroup.FirstOrDefault(x => AudioFormatHelper.GetAudioCodecFromExtension(x.Extension?.ToLowerInvariant() ?? Path.GetExtension(x.Filename) ?? "") != AudioFormat.Unknown);

            if (originalTrack == null)
                return false;

            _logger.Trace($"Expanding directory for: {folderData.Username}:{directoryGroup.Key}");

            SlskdRequestGenerator? requestGenerator = _indexer.GetExtendedRequestGenerator() as SlskdRequestGenerator;
            IGrouping<string, SlskdFileData>? expandedGroup = requestGenerator?.ExpandDirectory(folderData.Username, directoryGroup.Key, originalTrack).Result;

            if (expandedGroup != null)
            {
                _logger.Debug($"Successfully expanded directory to {expandedGroup.Count()} files");
                AlbumData albumData = SlskdItemsParser.CreateAlbumData(searchResponse.Id, expandedGroup, searchTextData, folderData, Settings);
                albumDatas.Add(albumData);
                return true;
            }
            else
            {
                _logger.Warn($"Failed to expand directory for {folderData.Username}:{directoryGroup.Key}");
            }
            return false;
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
            ExecuteRemovalAsync((SlskdSettings)_indexerFactory.Value.Get(message.Album.Release.IndexerId).Settings, selectedId).Wait();
            _interactiveResults.Remove(message.Album.Release.IndexerId);
        }

        public void Handle(ApplicationShutdownRequested message)
        {
            foreach (int indexerId in _interactiveResults.Keys.ToList())
            {
                if (_interactiveResults.TryGetValue(indexerId, out string? selectedId))
                {
                    ExecuteRemovalAsync((SlskdSettings)_indexerFactory.Value.Get(indexerId).Settings, selectedId).Wait();
                    _interactiveResults.Remove(indexerId);
                }
            }
        }

        public static void InvalidIgnoreCache(string path) => _ignoreListCache.Remove(path);

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