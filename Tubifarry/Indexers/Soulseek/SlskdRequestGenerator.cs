using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.IndexerSearch.Definitions;
using System.Net;

namespace NzbDrone.Core.Indexers.Soulseek
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
            IndexerPageableRequestChain chain = new();

            string searchQuery = $"{searchCriteria.AlbumQuery}  {searchCriteria.ArtistQuery}";
            chain.AddTier(GetRequests(searchQuery, "album"));
            return chain;
        }

        public IndexerPageableRequestChain GetSearchRequests(ArtistSearchCriteria searchCriteria)
        {
            _logger.Trace($"Generating search requests for artist: {searchCriteria.ArtistQuery}");
            IndexerPageableRequestChain chain = new();

            string searchQuery = $"  {searchCriteria.ArtistQuery}";
            chain.AddTier(GetRequests(searchQuery, "album"));
            return chain;
        }

        private IEnumerable<IndexerRequest> GetRequests(string searchQuery, string searchType)
        {
            var searchData = new
            {
                Id = Guid.NewGuid().ToString(),
                Settings.FileLimit,
                FilterResponses = true,
                Settings.MaximumPeerQueueLength,
                Settings.MinimumPeerUploadSpeed,
                Settings.MinimumResponseFileCount,
                Settings.ResponseLimit,
                SearchText = searchQuery,
                SearchTimeout = (int)((Settings.TimeoutInSeconds - 10) * 1000),
            };

            string jsonData = JsonConvert.SerializeObject(searchData);

            _logger.Info($"Sending search request with payload: {jsonData}");

            HttpRequest searchRequest = new HttpRequestBuilder($"{Settings.BaseUrl}/api/v0/searches")
                .SetHeader("X-API-KEY", Settings.ApiKey)
                .SetHeader("Content-Type", "application/json")
                .Post()
                .Build();

            searchRequest.SetContent(jsonData);
            _client.Execute(searchRequest);
            _ = WaitOnSearchCompletionAsync(searchData.Id, TimeSpan.FromSeconds(Settings.TimeoutInSeconds)).Result;

            _logger.Info($"Generated search initiation request: {searchRequest.Url}");

            HttpRequest request = new HttpRequestBuilder($"{Settings.BaseUrl}/api/v0/searches/{searchData.Id}")
                .AddQueryParam("includeResponses", true).SetHeader("X-API-KEY", Settings.ApiKey).Build();

            yield return new IndexerRequest(request);
        }

        private async Task<dynamic?> WaitOnSearchCompletionAsync(string searchId, TimeSpan timeout)
        {
            DateTime startTime = DateTime.UtcNow;
            string state = "InProgress";
            int totalFilesFound = 0;

            while (state == "InProgress")
            {
                TimeSpan elapsed = DateTime.UtcNow - startTime;
                if (elapsed > timeout)
                {
                    _logger.Warn($"Search timed out after {timeout.TotalSeconds} seconds.");
                    throw new TimeoutException("Search did not complete within the specified timeout.");
                }

                dynamic? searchStatus = await GetSearchResultsAsync(searchId);
                state = searchStatus?.state ?? "InProgress";

                int fileCount = (int)(searchStatus?.fileCount ?? 0);

                if (fileCount > totalFilesFound)
                    totalFilesFound = fileCount;

                double progress = Math.Clamp(fileCount / (double)Settings.FileLimit, 0.0, 1.0);
                double delay = CalculateQuadraticDelay(progress);

                _logger.Info($"Current progress: {progress:P0}, Files found: {fileCount}, Next delay: {delay} seconds");
                await Task.Delay(TimeSpan.FromSeconds(delay));

                if (state != "InProgress")
                    break;
            }

            _logger.Info($"Search completed with state: {state}, Total files found: {totalFilesFound}");

            return await GetSearchResultsAsync(searchId);
        }


        private static double CalculateQuadraticDelay(double progress)
        {
            double a = 16;
            double b = -16;
            double c = 5;

            double delay = a * Math.Pow(progress, 2) + b * progress + c;
            return Math.Clamp(delay, 0.5, 5);
        }

        private async Task<dynamic?> GetSearchResultsAsync(string searchId)
        {
            _searchResultsRequest ??= new HttpRequestBuilder($"{Settings.BaseUrl}/api/v0/searches/{searchId}")
                     .SetHeader("X-API-KEY", Settings.ApiKey)
                     .Build();

            HttpResponse response = await _client.ExecuteAsync(_searchResultsRequest);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                _logger.Warn($"Failed to fetch search results. Status: {response.StatusCode}, Content: {response.Content}");
                return null;
            }

            _logger.Info(response.Content);
            return JsonConvert.DeserializeObject<dynamic>(response.Content);
        }
    }
}