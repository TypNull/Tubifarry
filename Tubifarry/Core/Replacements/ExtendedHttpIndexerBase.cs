using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Exceptions;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using System.Net;

namespace Tubifarry.Core.Replacements
{
    /// <summary>
    /// Enhanced generic base class for HTTP indexers with advanced search and result handling
    /// </summary>
    public abstract class ExtendedHttpIndexerBase<TSettings, TIndexerPageableRequest> : IndexerBase<TSettings>
        where TSettings : IIndexerSettings, new()
        where TIndexerPageableRequest : IndexerPageableRequest
    {
        protected const int MaxNumResultsPerQuery = 1000;

        protected readonly IHttpClient _httpClient;
        protected new readonly Logger _logger = null!;

        public override bool SupportsRss { get; }
        public override bool SupportsSearch { get; }

        public abstract int PageSize { get; }
        public abstract TimeSpan RateLimit { get; }

        public abstract IIndexerRequestGenerator<TIndexerPageableRequest> GetExtendedRequestGenerator();
        public abstract IParseIndexerResponse GetParser();

        protected ExtendedHttpIndexerBase(
            IHttpClient httpClient,
            IIndexerStatusService indexerStatusService,
            IConfigService configService,
            IParsingService parsingService,
            Logger logger)
            : base(indexerStatusService, configService, parsingService, logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        // Concrete implementation of inherited abstract methods
        public override async Task<IList<ReleaseInfo>> FetchRecent()
        {
            if (!SupportsRss)
                return Array.Empty<ReleaseInfo>();

            return await FetchReleases(g => g.GetRecentRequests(), true);
        }

        public override async Task<IList<ReleaseInfo>> Fetch(AlbumSearchCriteria searchCriteria)
        {
            if (!SupportsSearch)
                return Array.Empty<ReleaseInfo>();

            return await FetchReleases(g => g.GetSearchRequests(searchCriteria));
        }

        public override async Task<IList<ReleaseInfo>> Fetch(ArtistSearchCriteria searchCriteria)
        {
            if (!SupportsSearch) Array.Empty<ReleaseInfo>();

            return await FetchReleases(g => g.GetSearchRequests(searchCriteria));
        }

        public override HttpRequest GetDownloadRequest(string link) => new(link);


        protected virtual async Task<IList<ReleaseInfo>> FetchReleases(
            Func<IIndexerRequestGenerator<TIndexerPageableRequest>, IndexerPageableRequestChain<TIndexerPageableRequest>> pageableRequestChainSelector,
            bool isRecent = false)
        {
            List<ReleaseInfo> releases = new();
            string url = string.Empty;
            TimeSpan minimumBackoff = TimeSpan.FromHours(1);

            try
            {
                IIndexerRequestGenerator<TIndexerPageableRequest> generator = GetExtendedRequestGenerator();
                IParseIndexerResponse parser = GetParser();

                IndexerPageableRequestChain<TIndexerPageableRequest> pageableRequestChain = pageableRequestChainSelector(generator);

                bool fullyUpdated = false;
                ReleaseInfo? lastReleaseInfo = null;
                if (isRecent)
                {
                    lastReleaseInfo = _indexerStatusService.GetLastRssSyncReleaseInfo(Definition.Id);
                }

                for (int i = 0; i < pageableRequestChain.Tiers; i++)
                {
                    IEnumerable<TIndexerPageableRequest> pageableRequests = pageableRequestChain.GetTier(i);

                    List<ReleaseInfo> tierReleases = new();

                    foreach (TIndexerPageableRequest pageableRequest in pageableRequests)
                    {
                        List<ReleaseInfo> pagedReleases = new();

                        foreach (IndexerRequest? request in pageableRequest)
                        {
                            url = request.Url.FullUri;

                            IList<ReleaseInfo> page = await FetchPage(request, parser);

                            pagedReleases.AddRange(page);

                            if (ShouldStopFetchingPages(isRecent, page, lastReleaseInfo, pagedReleases, ref fullyUpdated))
                                break;

                            if (!IsFullPage(page))
                                break;
                        }

                        tierReleases.AddRange(pagedReleases.Where(IsValidRelease));
                    }

                    releases.AddRange(tierReleases);

                    if (pageableRequestChain.AreTierResultsUsable(i, tierReleases.Count))
                    {
                        _logger.Debug($"Tier {i + 1} found {tierReleases.Count} usable results out of total {releases.Count} results. Stopping search.");
                        break;
                    }
                    else if (tierReleases.Any())
                        _logger.Debug($"Tier {i + 1} found {tierReleases.Count} results out of total {releases.Count}, but doesn't meet usability criteria. Trying next tier.");

                    else
                        _logger.Debug($"Tier {i + 1} found no results. Total results so far: {releases.Count}. Trying next tier.");

                }

                if (isRecent && !releases.Empty())
                    UpdateRssSyncStatus(releases, lastReleaseInfo, fullyUpdated);

                _indexerStatusService.RecordSuccess(Definition.Id);
            }
            catch (Exception ex)
            {
                HandleException(ex, url, minimumBackoff);
            }

            return CleanupReleases(releases, isRecent);
        }

        protected virtual bool ShouldStopFetchingPages(bool isRecent, IList<ReleaseInfo> page, ReleaseInfo? lastReleaseInfo, List<ReleaseInfo> pagedReleases, ref bool fullyUpdated)
        {
            if (!isRecent)
                return pagedReleases.Count >= MaxNumResultsPerQuery;

            if (!page.Any())
                return false;

            if (lastReleaseInfo == null)
            {
                fullyUpdated = true;
                return true;
            }

            DateTime oldestReleaseDate = page.Select(v => v.PublishDate).Min();
            if (oldestReleaseDate < lastReleaseInfo.PublishDate || page.Any(v => v.DownloadUrl == lastReleaseInfo.DownloadUrl))
            {
                fullyUpdated = true;
                return true;
            }

            if (pagedReleases.Count >= MaxNumResultsPerQuery && oldestReleaseDate < DateTime.UtcNow - TimeSpan.FromHours(24))
            {
                fullyUpdated = false;
                return true;
            }

            return false;
        }

        protected virtual bool IsFullPage(IList<ReleaseInfo> page) => PageSize != 0 && page.Count >= PageSize;


        protected virtual bool IsValidRelease(ReleaseInfo release)
        {
            if (release.Title.IsNullOrWhiteSpace())
            {
                _logger.Trace("Invalid Release: '{0}' from indexer: {1}. No title provided.", release.InfoUrl, Definition.Name);
                return false;
            }

            if (release.DownloadUrl.IsNullOrWhiteSpace())
            {
                _logger.Trace("Invalid Release: '{0}' from indexer: {1}. No Download URL provided.", release.Title, Definition.Name);
                return false;
            }

            return true;
        }

        private void UpdateRssSyncStatus(List<ReleaseInfo> releases, ReleaseInfo? lastReleaseInfo, bool fullyUpdated)
        {
            List<ReleaseInfo> ordered = releases.OrderByDescending(v => v.PublishDate).ToList();

            if (!fullyUpdated && lastReleaseInfo != null)
            {
                DateTime gapStart = lastReleaseInfo.PublishDate;
                DateTime gapEnd = ordered.Last().PublishDate;
                _logger.Warn("Indexer {0} rss sync didn't cover the period between {1} and {2} UTC. Search may be required.", Definition.Name, gapStart, gapEnd);
            }

            lastReleaseInfo = ordered.First();
            _indexerStatusService.UpdateRssSyncStatus(Definition.Id, lastReleaseInfo);
        }

        private void HandleException(Exception ex, string url, TimeSpan minimumBackoff)
        {
            switch (ex)
            {
                case WebException webException:
                    HandleWebException(webException, url);
                    break;
                case TooManyRequestsException tooManyRequestsEx:
                    HandleTooManyRequestsException(tooManyRequestsEx, minimumBackoff);
                    break;
                case HttpException httpException:
                    HandleHttpException(httpException, url);
                    break;
                case RequestLimitReachedException requestLimitEx:
                    HandleRequestLimitReachedException(requestLimitEx, minimumBackoff);
                    break;
                case ApiKeyException:
                    _indexerStatusService.RecordFailure(Definition.Id);
                    _logger.Warn("Invalid API Key for {0} {1}", this, url);
                    break;
                case IndexerException indexerEx:
                    _indexerStatusService.RecordFailure(Definition.Id);
                    _logger.Warn(indexerEx, "{0}", url);
                    break;
                case TaskCanceledException taskCancelledEx:
                    _indexerStatusService.RecordFailure(Definition.Id);
                    _logger.Warn(taskCancelledEx, "Unable to connect to indexer, possibly due to a timeout. {0}", url);
                    break;
                default:
                    _indexerStatusService.RecordFailure(Definition.Id);
                    ex.WithData("FeedUrl", url);
                    _logger.Error(ex, "An error occurred while processing feed. {0}", url);
                    break;
            }
        }

        private void HandleWebException(WebException webException, string url)
        {
            if (webException.Status is WebExceptionStatus.NameResolutionFailure or WebExceptionStatus.ConnectFailure)
                _indexerStatusService.RecordConnectionFailure(Definition.Id);
            else
                _indexerStatusService.RecordFailure(Definition.Id);

            if (webException.Message.Contains("502") || webException.Message.Contains("503") || webException.Message.Contains("504") || webException.Message.Contains("timed out"))
                _logger.Warn("{0} server is currently unavailable. {1} {2}", this, url, webException.Message);
            else
                _logger.Warn("{0} {1} {2}", this, url, webException.Message);
        }

        private void HandleHttpException(HttpException ex, string url)
        {
            _indexerStatusService.RecordFailure(Definition.Id);
            if (ex.Response.HasHttpServerError)
                _logger.Warn("Unable to connect to {0} at [{1}]. Indexer's server is unavailable. Try again later. {2}", this, url, ex.Message);
            else
                _logger.Warn("{0} {1}", this, ex.Message);
        }

        private void HandleTooManyRequestsException(TooManyRequestsException ex, TimeSpan minimumBackoff)
        {
            TimeSpan retryTime = ex.RetryAfter != TimeSpan.Zero ? ex.RetryAfter : minimumBackoff;
            _indexerStatusService.RecordFailure(Definition.Id, retryTime);

            _logger.Warn("API Request Limit reached for {0}. Disabled for {1}", this, retryTime);
        }

        private void HandleRequestLimitReachedException(RequestLimitReachedException ex, TimeSpan minimumBackoff)
        {
            TimeSpan retryTime = ex.RetryAfter != TimeSpan.Zero ? ex.RetryAfter : minimumBackoff;
            _indexerStatusService.RecordFailure(Definition.Id, retryTime);

            _logger.Warn("API Request Limit reached for {0}. Disabled for {1}", this, retryTime);
        }

        protected virtual async Task<IList<ReleaseInfo>> FetchPage(IndexerRequest request, IParseIndexerResponse parser)
        {
            IndexerResponse response = await FetchIndexerResponse(request);

            try
            {
                return parser.ParseResponse(response).ToList();
            }
            catch (Exception ex)
            {
                ex.WithData(response.HttpResponse, 128 * 1024);
                _logger.Trace("Unexpected Response content ({0} bytes): {1}", response.HttpResponse.ResponseData.Length, response.HttpResponse.Content);
                throw;
            }
        }

        protected virtual async Task<IndexerResponse> FetchIndexerResponse(IndexerRequest request)
        {
            _logger.Debug("Downloading Feed " + request.HttpRequest.ToString(false));

            if (request.HttpRequest.RateLimit < RateLimit)
                request.HttpRequest.RateLimit = RateLimit;


            request.HttpRequest.RateLimitKey = Definition.Id.ToString();

            HttpResponse response = await _httpClient.ExecuteAsync(request.HttpRequest);

            return new IndexerResponse(request, response);
        }

        protected override async Task Test(List<ValidationFailure> failures) => failures.AddIfNotNull(await TestConnection());


        protected virtual async Task<ValidationFailure> TestConnection()
        {
            try
            {
                IParseIndexerResponse parser = GetParser();
                IIndexerRequestGenerator<TIndexerPageableRequest> generator = GetExtendedRequestGenerator();
                IndexerRequest? firstRequest = generator.GetRecentRequests().GetAllTiers().FirstOrDefault()?.FirstOrDefault();

                if (firstRequest == null)
                {
                    return new ValidationFailure(string.Empty, "No rss feed query available. This may be an issue with the indexer or your indexer category settings.");
                }

                IList<ReleaseInfo> releases = await FetchPage(firstRequest, parser);

                if (releases.Empty())
                {
                    return new ValidationFailure(string.Empty, "Query successful, but no results in the configured categories were returned from your indexer. This may be an issue with the indexer or your indexer category settings.");
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to connect to indexer");
                return new ValidationFailure(string.Empty, $"Unable to connect to indexer: {ex.Message}");
            }

            return null!;
        }
    }
}