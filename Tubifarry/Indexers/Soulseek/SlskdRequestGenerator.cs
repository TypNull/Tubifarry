using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using System.Net;

namespace Tubifarry.Indexers.Soulseek
{
    internal class SlskdRequestGenerator : IIndexerRequestGenerator
    {
        private readonly SlskdIndexer _indexer;
        private readonly Logger _logger;
        private SlskdSettings Settings => _indexer.Settings;
        private readonly IHttpClient _client;

        private HttpRequest? _searchResultsRequest;

        public SlskdRequestGenerator(SlskdIndexer indexer, IHttpClient client)
        {
            _indexer = indexer;
            _client = client;
            _logger = NzbDroneLogger.GetLogger(this);
        }

        public IndexerPageableRequestChain GetRecentRequests() => new();

        public IndexerPageableRequestChain GetSearchRequests(AlbumSearchCriteria searchCriteria)
        {
            _logger.Trace($"Generating search requests for album: {searchCriteria.AlbumQuery} by artist: {searchCriteria.ArtistQuery}");
            int tarckCount = searchCriteria.Albums.FirstOrDefault()?.AlbumReleases.Value.Min(x => x.TrackCount) ?? 0;
            IndexerPageableRequestChain chain = new();

            chain.AddTier(DeferredGetRequests(searchCriteria.ArtistQuery, searchCriteria.AlbumQuery, searchCriteria.InteractiveSearch, tarckCount));

            if (!Settings.UseFallbackSearch)
                return chain;

            List<string> aliases = searchCriteria.Artist.Metadata.Value.Aliases;
            for (int i = 0; i < 2 && i < aliases.Count; i++)
                if (aliases[i].Length > 3)
                    chain.AddTier(DeferredGetRequests(aliases[i], searchCriteria.AlbumQuery, searchCriteria.InteractiveSearch, tarckCount));
            if (searchCriteria.AlbumQuery.Length > 20)
            {
                string[] albumWords = searchCriteria.AlbumQuery.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                int halfLength = (int)Math.Ceiling(albumWords.Length / 2.0);
                string halfAlbumTitle = string.Join(" ", albumWords.Take(halfLength));
                chain.AddTier(DeferredGetRequests(searchCriteria.CleanArtistQuery, halfAlbumTitle, searchCriteria.InteractiveSearch, tarckCount));
            }
            chain.AddTier(DeferredGetRequests(searchCriteria.CleanArtistQuery, null, searchCriteria.InteractiveSearch, tarckCount));
            chain.AddTier(DeferredGetRequests(null, searchCriteria.AlbumQuery, searchCriteria.InteractiveSearch, tarckCount));
            return chain;
        }


        public IndexerPageableRequestChain GetSearchRequests(ArtistSearchCriteria searchCriteria)
        {
            _logger.Debug($"Generating search requests for artist: {searchCriteria.CleanArtistQuery}");
            int tarckCount = searchCriteria.Albums.FirstOrDefault()?.AlbumReleases.Value.Min(x => x.TrackCount) ?? 0;
            IndexerPageableRequestChain chain = new();
            chain.AddTier(DeferredGetRequests(searchCriteria.CleanArtistQuery, null, searchCriteria.InteractiveSearch, tarckCount));

            List<string> aliases = searchCriteria.Artist.Metadata.Value.Aliases;
            for (int i = 0; i < 3 && i < aliases.Count && Settings.UseFallbackSearch; i++)
            {
                if (aliases[i].Length > 3)
                    chain.AddTier(DeferredGetRequests(aliases[i], null, searchCriteria.InteractiveSearch, tarckCount));
            }
            return chain;
        }


        private IEnumerable<IndexerRequest> DeferredGetRequests(string? artist, string? album, bool interactive, int trackCount, string? fullAlbum = null)
        {
            _searchResultsRequest = null;
            IndexerRequest? request = GetRequestsAsync(artist, album, interactive, trackCount, fullAlbum).Result;
            if (request != null)
                yield return request;
        }

        private async Task<IndexerRequest?> GetRequestsAsync(string? artist, string? album, bool interactive, int trackCount, string? fullAlbum = null)
        {
            try
            {
                _logger.Info($"Search: {artist} {album}");
                var searchData = new
                {
                    Id = Guid.NewGuid().ToString(),
                    Settings.FileLimit,
                    FilterResponses = true,
                    Settings.MaximumPeerQueueLength,
                    Settings.MinimumPeerUploadSpeed,
                    Settings.MinimumResponseFileCount,
                    Settings.ResponseLimit,
                    SearchText = $"{album} {artist}",
                    SearchTimeout = (int)(Settings.TimeoutInSeconds * 1000),
                };

                HttpRequest searchRequest = new HttpRequestBuilder($"{Settings.BaseUrl}/api/v0/searches")
                    .SetHeader("X-API-KEY", Settings.ApiKey)
                    .SetHeader("Content-Type", "application/json")
                    .Post()
                    .Build();

                searchRequest.SetContent(JsonConvert.SerializeObject(searchData));
                await _client.ExecuteAsync(searchRequest);
                await WaitOnSearchCompletionAsync(searchData.Id, TimeSpan.FromSeconds(Settings.TimeoutInSeconds));

                _logger.Trace($"Generated search initiation request: {searchRequest.Url}");

                HttpRequest request = new HttpRequestBuilder($"{Settings.BaseUrl}/api/v0/searches/{searchData.Id}")
                    .AddQueryParam("includeResponses", true)
                    .SetHeader("X-API-KEY", Settings.ApiKey)
                    .Build();
                request.ContentSummary = new
                {
                    Album = fullAlbum ?? album ?? "",
                    Artist = artist,
                    Interactive = interactive,
                    MimimumFiles = Math.Max(Settings.MinimumResponseFileCount, Settings.FilterLessFilesThanAlbum ? trackCount : 1)
                }.ToJson();

                return new IndexerRequest(request);
            }
            catch (HttpException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.Warn($"Search request failed for artist: {artist}, album: {album}. Error: {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"An error occurred while generating search request for artist: {artist}, album: {album}");
                return null;
            }
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
                    break;

                dynamic? searchStatus = await GetSearchResultsAsync(searchId);
                state = searchStatus?.state ?? "InProgress";

                int fileCount = (int)(searchStatus?.fileCount ?? 0);
                if (fileCount > totalFilesFound)
                    totalFilesFound = fileCount;

                double progress = Math.Clamp(fileCount / (double)Settings.FileLimit, 0.0, 1.0);
                double delay;

                if (hasTimedOut && DateTime.UtcNow < timeoutEndTime)
                    delay = 1;
                else
                    delay = CalculateQuadraticDelay(progress);

                await Task.Delay(TimeSpan.FromSeconds(delay));
                if (state != "InProgress")
                    break;
            }

            _logger.Trace($"Search completed with state: {state}, Total files found: {totalFilesFound}");
            return;
        }


        private static double CalculateQuadraticDelay(double progress)
        {
            const double a = 16;
            const double b = -16;
            const double c = 5;

            double delay = (a * Math.Pow(progress, 2)) + (b * progress) + c;
            return Math.Clamp(delay, 0.5, 5);
        }

        private async Task<dynamic?> GetSearchResultsAsync(string searchId)
        {
            _searchResultsRequest ??= new HttpRequestBuilder($"{Settings.BaseUrl}/api/v0/searches/{searchId}")
                     .SetHeader("X-API-KEY", Settings.ApiKey).Build();
            HttpResponse response = await _client.ExecuteAsync(_searchResultsRequest);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                _logger.Warn($"Failed to fetch search results. Status: {response.StatusCode}, Content: {response.Content}");
                return null;
            }
            return JsonConvert.DeserializeObject<dynamic>(response.Content);
        }
    }
}