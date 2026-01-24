using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Music;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tubifarry.Core.Replacements;
using Tubifarry.Core.Utilities;
using Tubifarry.Indexers.Soulseek.Search.Core;

namespace Tubifarry.Indexers.Soulseek
{
    internal class SlskdRequestGenerator : IIndexerRequestGenerator<LazyIndexerPageableRequest>
    {
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
        private readonly SlskdIndexer _indexer;
        private readonly Logger _logger;
        private readonly IHttpClient _client;
        private readonly ISlskdSearchChain _searchPipeline;
        private readonly HashSet<string> _processedSearches = new(StringComparer.OrdinalIgnoreCase);

        private SlskdSettings Settings => _indexer.Settings;

        public SlskdRequestGenerator(SlskdIndexer indexer, ISlskdSearchChain searchPipeline, IHttpClient client)
        {
            _indexer = indexer;
            _client = client;
            _searchPipeline = searchPipeline;
            _logger = NzbDroneLogger.GetLogger(this);
        }

        public IndexerPageableRequestChain<LazyIndexerPageableRequest> GetRecentRequests() => new LazyIndexerPageableRequestChain(Settings.MinimumResults);

        public IndexerPageableRequestChain<LazyIndexerPageableRequest> GetSearchRequests(AlbumSearchCriteria searchCriteria)
        {
            _logger.Trace($"Setting up lazy search for album: {searchCriteria.AlbumQuery} by artist: {searchCriteria.ArtistQuery}");

            Album? album = searchCriteria.Albums.FirstOrDefault();
            List<AlbumRelease>? albumReleases = album?.AlbumReleases.Value;
            int trackCount = albumReleases?.Any() == true ? albumReleases.Min(x => x.TrackCount) : 0;
            List<string> tracks = albumReleases?.FirstOrDefault(x => x.Tracks.Value is { Count: > 0 })?.Tracks.Value?
                .Where(x => !string.IsNullOrEmpty(x.Title)).Select(x => x.Title).ToList() ?? [];

            _processedSearches.Clear();

            SearchContext context = new(
                Artist: searchCriteria.ArtistQuery,
                Album: searchCriteria.ArtistQuery != searchCriteria.AlbumQuery ? searchCriteria.AlbumQuery : null,
                Year: searchCriteria.AlbumYear.ToString(),
                PrimaryType: GetPrimaryAlbumType(album?.AlbumType),
                Interactive: searchCriteria.InteractiveSearch,
                TrackCount: trackCount,
                Aliases: searchCriteria.Artist?.Metadata.Value.Aliases ?? [],
                Tracks: tracks,
                Settings: Settings,
                ProcessedSearches: _processedSearches,
                SearchCriteria: searchCriteria);

            return _searchPipeline.BuildChain(context, ExecuteSearch);
        }

        public IndexerPageableRequestChain<LazyIndexerPageableRequest> GetSearchRequests(ArtistSearchCriteria searchCriteria)
        {
            _logger.Debug($"Setting up lazy search for artist: {searchCriteria.CleanArtistQuery}");

            Album? album = searchCriteria.Albums.FirstOrDefault();
            List<AlbumRelease>? albumReleases = album?.AlbumReleases.Value;
            int trackCount = albumReleases?.Any() == true ? albumReleases.Min(x => x.TrackCount) : 0;
            List<string> tracks = albumReleases?.FirstOrDefault(x => x.Tracks.Value is { Count: > 0 })?.Tracks.Value?
                .Where(x => !string.IsNullOrEmpty(x.Title)).Select(x => x.Title).ToList() ?? [];

            _processedSearches.Clear();

            SearchContext context = new(
                Artist: searchCriteria.CleanArtistQuery,
                Album: null,
                Year: null,
                PrimaryType: GetPrimaryAlbumType(album?.AlbumType),
                Interactive: searchCriteria.InteractiveSearch,
                TrackCount: trackCount,
                Aliases: searchCriteria.Artist?.Metadata.Value.Aliases ?? [],
                Tracks: tracks,
                Settings: Settings,
                ProcessedSearches: _processedSearches,
                SearchCriteria: searchCriteria);

            return _searchPipeline.BuildChain(context, ExecuteSearch);
        }

        private IEnumerable<IndexerRequest> ExecuteSearch(SearchQuery query)
        {
            string? searchText = query.SearchText ?? SlskdTextProcessor.BuildSearchText(query.Artist, query.Album);

            if (string.IsNullOrWhiteSpace(searchText))
                return [];

            try
            {
                IndexerRequest? request = GetRequestsAsync(query, searchText).GetAwaiter().GetResult();
                if (request != null)
                {
                    _logger.Trace($"Successfully generated request for search: {searchText}");
                    return [request];
                }
                else
                {
                    _logger.Trace($"GetRequestsAsync returned null for search: {searchText}");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error executing search: {searchText}");
            }

            return [];
        }

        private async Task<IndexerRequest?> GetRequestsAsync(SearchQuery query, string searchText)
        {
            try
            {
                _logger.Debug($"Search: {searchText}");

                dynamic searchData = CreateSearchData(searchText);
                dynamic searchId = searchData.Id;
                dynamic searchRequest = CreateSearchRequest(searchData);

                await ExecuteSearchAsync(searchRequest, searchId);

                dynamic request = CreateResultRequest(searchId, query);
                return new IndexerRequest(request);
            }
            catch (HttpException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.Warn($"Search request failed for: {searchText}. Error: {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error generating search request for: {searchText}");
                return null;
            }
        }

        private dynamic CreateSearchData(string searchText) => new
        {
            Id = Guid.NewGuid().ToString(),
            Settings.FileLimit,
            FilterResponses = true,
            Settings.MaximumPeerQueueLength,
            Settings.MinimumPeerUploadSpeed,
            Settings.MinimumResponseFileCount,
            Settings.ResponseLimit,
            SearchText = searchText,
            SearchTimeout = (int)(Settings.TimeoutInSeconds * 1000),
        };

        private HttpRequest CreateSearchRequest(dynamic searchData)
        {
            HttpRequest searchRequest = new HttpRequestBuilder($"{Settings.BaseUrl}/api/v0/searches")
                .SetHeader("X-API-KEY", Settings.ApiKey)
                .SetHeader("Content-Type", "application/json")
                .Post()
                .Build();

            searchRequest.SetContent(JsonSerializer.Serialize(searchData));
            return searchRequest;
        }

        private async Task ExecuteSearchAsync(HttpRequest searchRequest, string searchId)
        {
            await _client.ExecuteAsync(searchRequest);
            await WaitOnSearchCompletionAsync(searchId, TimeSpan.FromSeconds(Settings.TimeoutInSeconds));
        }

        private HttpRequest CreateResultRequest(string searchId, SearchQuery query)
        {
            HttpRequest request = new HttpRequestBuilder($"{Settings.BaseUrl}/api/v0/searches/{searchId}")
                .AddQueryParam("includeResponses", true)
                .SetHeader("X-API-KEY", Settings.ApiKey)
                .Build();

            request.ContentSummary = new
            {
                Album = query.Album ?? "",
                Artist = query.Artist,
                Interactive = query.Interactive,
                ExpandDirectory = query.ExpandDirectory,
                MimimumFiles = Math.Max(Settings.MinimumResponseFileCount, Settings.FilterUnfittingAlbums ? query.TrackCount : 1),
                MaximumFiles = Settings.FilterUnfittingAlbums ? (int?)(query.TrackCount + Math.Max(2, (int)Math.Ceiling(Math.Log(query.TrackCount) * 1.67))) : null
            }.ToJson();

            return request;
        }

        private async Task WaitOnSearchCompletionAsync(string searchId, TimeSpan timeout)
        {
            DateTime startTime = DateTime.UtcNow.AddSeconds(2);
            string state = "InProgress";
            int totalFilesFound = 0;
            bool hasTimedOut = false;
            DateTime timeoutEndTime = DateTime.UtcNow;

            while (state == "InProgress")
            {
                TimeSpan elapsed = DateTime.UtcNow - startTime;

                if (elapsed > timeout && !hasTimedOut)
                {
                    hasTimedOut = true;
                    timeoutEndTime = DateTime.UtcNow.AddSeconds(20);
                }
                else if (hasTimedOut && timeoutEndTime < DateTime.UtcNow)
                {
                    break;
                }

                JsonNode? searchStatus = await GetSearchResultsAsync(searchId);

                state = searchStatus?["state"]?.GetValue<string>() ?? "InProgress";
                int fileCount = searchStatus?["fileCount"]?.GetValue<int>() ?? 0;

                if (fileCount > totalFilesFound)
                    totalFilesFound = fileCount;

                double progress = Math.Clamp(fileCount / (double)Settings.FileLimit, 0.0, 1.0);
                double delay = hasTimedOut && DateTime.UtcNow < timeoutEndTime ? 1.0 : CalculateQuadraticDelay(progress);

                await Task.Delay(TimeSpan.FromSeconds(delay));
                if (state != "InProgress")
                    break;
            }
        }

        private async Task<JsonNode?> GetSearchResultsAsync(string searchId)
        {
            HttpRequest searchResultsRequest = new HttpRequestBuilder($"{Settings.BaseUrl}/api/v0/searches/{searchId}")
                     .SetHeader("X-API-KEY", Settings.ApiKey).Build();

            HttpResponse response = await _client.ExecuteAsync(searchResultsRequest);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                _logger.Warn($"Failed to fetch search results for ID {searchId}. Status: {response.StatusCode}, Content: {response.Content}");
                return null;
            }

            return JsonSerializer.Deserialize<JsonNode>(response.Content);
        }

        private static double CalculateQuadraticDelay(double progress)
        {
            const double a = 16;
            const double b = -16;
            const double c = 5;

            double delay = (a * Math.Pow(progress, 2)) + (b * progress) + c;
            return Math.Clamp(delay, 0.5, 5);
        }

        private static PrimaryAlbumType GetPrimaryAlbumType(string? albumType)
        {
            if (string.IsNullOrWhiteSpace(albumType))
                return PrimaryAlbumType.Album;

            PrimaryAlbumType? matchedType = PrimaryAlbumType.All
                .FirstOrDefault(t => t.Name.Equals(albumType, StringComparison.OrdinalIgnoreCase));

            return matchedType ?? PrimaryAlbumType.Album;
        }

        public async Task<IGrouping<string, SlskdFileData>?> ExpandDirectory(string username, string directoryPath, SlskdFileData originalTrack)
        {
            try
            {
                HttpRequest request = new HttpRequestBuilder($"{Settings.BaseUrl}/api/v0/users/{Uri.EscapeDataString(username)}/directory")
                    .SetHeader("X-API-KEY", Settings.ApiKey)
                    .SetHeader("Content-Type", "application/json")
                    .Post()
                    .Build();

                request.SetContent(JsonSerializer.Serialize(new { directory = directoryPath }));

                HttpResponse response = await _client.ExecuteAsync(request);

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    SlskdDirectoryApiResponse[]? directoryResponse = JsonSerializer.Deserialize<SlskdDirectoryApiResponse[]>(response.Content, _jsonOptions);

                    if (directoryResponse?.Length > 0 && directoryResponse[0].Files?.Any() == true)
                    {
                        string originalExtension = originalTrack.Extension?.ToLowerInvariant() ?? "";

                        List<SlskdFileData> directoryFiles = directoryResponse[0].Files
                            .Where(f => AudioFormatHelper.GetAudioCodecFromExtension(Path.GetExtension(f.Filename)) != AudioFormat.Unknown)
                            .Select(f =>
                            {
                                string fileExtension = Path.GetExtension(f.Filename)?.TrimStart('.').ToLowerInvariant() ?? "";
                                bool sameExtension = fileExtension == originalExtension;

                                return new SlskdFileData(
                                    Filename: $"{directoryPath}\\{f.Filename}",
                                    BitRate: sameExtension ? originalTrack.BitRate : null,
                                    BitDepth: sameExtension ? originalTrack.BitDepth : null,
                                    Size: f.Size,
                                    Length: sameExtension ? originalTrack.Length : null,
                                    Extension: fileExtension,
                                    SampleRate: sameExtension ? originalTrack.SampleRate : null,
                                    Code: f.Code,
                                    IsLocked: false
                                );
                            }).ToList();

                        if (directoryFiles.Count != 0)
                            return directoryFiles.GroupBy(f => SlskdTextProcessor.GetDirectoryFromFilename(f.Filename)).First();
                    }
                }
                else
                {
                    _logger.Debug($"Directory API returned {response.StatusCode} for {username}:{directoryPath}");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error expanding directory {username}:{directoryPath}");
            }

            return null;
        }
    }
}
