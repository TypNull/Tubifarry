using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Exceptions;
using NzbDrone.Core.Indexers.Exceptions;
using NzbDrone.Core.Parser;
using System.Net;

namespace Tubifarry.ImportLists.LastFmRecommendation
{
    internal class LastFmRecommend : HttpImportListBase<LastFmRecommendSettings>
    {
        private readonly IHttpClient _client;
        public override string Name => "Last.fm Recommend";
        public override TimeSpan MinRefreshInterval => TimeSpan.FromDays(7);
        public override ImportListType ListType => ImportListType.LastFm;

        public override int PageSize => 100;
        public override TimeSpan RateLimit => TimeSpan.FromSeconds(5);

        public LastFmRecommend(IHttpClient httpClient, IImportListStatusService importListStatusService, IConfigService configService, IParsingService parsingService, Logger logger) : base(httpClient, importListStatusService, configService, parsingService, logger) => _client = httpClient;

        public override IImportListRequestGenerator GetRequestGenerator() => new LastFmRecomendRequestGenerator(Settings);

        public override IParseImportListResponse GetParser() => new LastFmRecommendParser(Settings, _client);

        protected override void Test(List<ValidationFailure> failures)
        {
            failures.AddIfNotNull(TestConnection());
        }

        protected override ValidationFailure? TestConnection()
        {
            try
            {
                IImportListRequestGenerator generator = GetRequestGenerator();
                ImportListRequest listItems = generator.GetListItems().GetAllTiers().First().First();
                ImportListResponse response = FetchImportListResponse(listItems);

                // Validate HTTP status first
                if (response.HttpResponse.StatusCode != HttpStatusCode.OK)
                {
                    return new ValidationFailure(string.Empty, "Connection failed: Server returned HTTP " +
                        $"{(int)response.HttpResponse.StatusCode} ({response.HttpResponse.StatusCode})");
                }

                // Enhanced content type validation
                string? contentType = response.HttpResponse.Headers.ContentType;
                if (contentType == null ||
                    !IsJsonContentType(contentType))
                {
                    string receivedType = contentType ?? "null/no-content-type";
                    return new ValidationFailure(string.Empty, $"Unexpected content type: {receivedType}. " + "Server must return JSON (application/json or similar)");
                }
                return null;
            }
            catch (RequestLimitReachedException)
            {
                _logger.Warn("API request limit reached");
                return new ValidationFailure(string.Empty, "API rate limit exceeded - try again later");
            }
            catch (UnsupportedFeedException ex)
            {
                _logger.Warn(ex, "Feed format not supported");
                return new ValidationFailure(string.Empty, $"Unsupported feed format: {ex.Message}");
            }
            catch (ImportListException ex)
            {
                _logger.Warn(ex, "Connection failed");
                return new ValidationFailure(string.Empty, $"Connection error: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Critical connection failure");
                return new ValidationFailure(string.Empty, "Configuration error - check logs for details");
            }
        }

        private static bool IsJsonContentType(string mediaType) => mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
                   mediaType.Equals("text/json", StringComparison.OrdinalIgnoreCase) ||
                   mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);

    }
}
