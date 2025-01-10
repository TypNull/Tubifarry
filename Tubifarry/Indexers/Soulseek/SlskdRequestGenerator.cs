using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.IndexerSearch.Definitions;
using System.Net;
using System.Text;

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
            chain.AddTier(GetRequests(searchCriteria.ArtistQuery, searchCriteria.AlbumQuery, searchCriteria.InteractiveSearch));
            return chain;
        }

        public IndexerPageableRequestChain GetSearchRequests(ArtistSearchCriteria searchCriteria)
        {
            _logger.Trace($"Generating search requests for artist: {searchCriteria.ArtistQuery}");
            IndexerPageableRequestChain chain = new();
            chain.AddTier(GetRequests(searchCriteria.ArtistQuery, null, searchCriteria.InteractiveSearch));
            return chain;
        }

        private IEnumerable<IndexerRequest> GetRequests(string artist, string? album = null, bool interactive = false)
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
                SearchText = $"{album} {artist}",
                SearchTimeout = (int)(Settings.TimeoutInSeconds * 1000),
            };

            HttpRequest searchRequest = new HttpRequestBuilder($"{Settings.BaseUrl}/api/v0/searches")
                .SetHeader("X-API-KEY", Settings.ApiKey)
                .SetHeader("Content-Type", "application/json")
                .Post()
                .Build();

            searchRequest.SetContent(JsonConvert.SerializeObject(searchData));
            _client.Execute(searchRequest);
            WaitOnSearchCompletionAsync(searchData.Id, TimeSpan.FromSeconds(Settings.TimeoutInSeconds)).Wait();

            _logger.Trace($"Generated search initiation request: {searchRequest.Url}");

            HttpRequest request = new HttpRequestBuilder($"{Settings.BaseUrl}/api/v0/searches/{searchData.Id}")
                .AddQueryParam("includeResponses", true)
                .SetHeader("X-API-KEY", Settings.ApiKey)
                .SetHeader("X-ALBUM", Convert.ToBase64String(Encoding.UTF8.GetBytes(album ?? "")))
                .SetHeader("X-ARTIST", Convert.ToBase64String(Encoding.UTF8.GetBytes(artist)))
                .SetHeader("X-INTERACTIVE", interactive.ToString())
                .Build();
            yield return new IndexerRequest(request);
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
            return JsonConvert.DeserializeObject<dynamic>(response.Content);
        }
    }
}