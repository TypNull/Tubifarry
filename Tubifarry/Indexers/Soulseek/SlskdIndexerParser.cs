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

namespace Tubifarry.Indexers.Soulseek
{
    public class SlskdIndexerParser : IParseIndexerResponse, IHandle<AlbumGrabbedEvent>, IHandle<ApplicationShutdownRequested>
    {
        private readonly SlskdIndexer _indexer;
        private readonly Logger _logger;
        private readonly Lazy<IIndexerFactory> _indexerFactory;
        private readonly IHttpClient _httpClient;

        private static Dictionary<int, string> _interactiveResults = new();

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
            List<AlbumData> albumDatas = new();
            try
            {
                SlskdSearchResponse? searchResponse = JsonSerializer.Deserialize<SlskdSearchResponse>(indexerResponse.Content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (searchResponse == null)
                {
                    _logger.Error("Failed to deserialize slskd search response.");
                    return new List<ReleaseInfo>();
                }

                SlskdSearchData searchTextData = SlskdSearchData.FromJson(indexerResponse.HttpRequest.ContentSummary);

                foreach (SlskdFolderData response in searchResponse.Responses)
                {
                    if (response.FileCount < searchTextData.MinimumFiles)
                        continue;

                    IEnumerable<SlskdFileData> filteredFiles = SlskdFileData.GetFilteredFiles(response.Files, Settings.OnlyAudioFiles, Settings.IncludeFileExtensions);

                    foreach (IGrouping<string, SlskdFileData> directoryGroup in filteredFiles.GroupBy(f => GetDirectoryFromFilename(f.Filename)))
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

                        AlbumData albumData = SlskdItemsParser.CreateAlbumData(searchResponse.Id, directoryGroup, searchTextData, folderData, Settings);

                        albumDatas.Add(albumData);
                    }
                }

                RemoveSearch(searchResponse.Id, albumDatas.Any() && searchTextData.Interactive);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to parse Slskd search response.");
            }

            return albumDatas.OrderByDescending(x => x.Priotity).Select(a => a.ToReleaseInfo()).ToList();
        }

        private static string GetDirectoryFromFilename(string? filename)
        {
            if (string.IsNullOrEmpty(filename))
                return "";
            int lastBackslashIndex = filename.LastIndexOf('\\');
            return lastBackslashIndex >= 0 ? filename[..lastBackslashIndex] : "";
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
    }
}