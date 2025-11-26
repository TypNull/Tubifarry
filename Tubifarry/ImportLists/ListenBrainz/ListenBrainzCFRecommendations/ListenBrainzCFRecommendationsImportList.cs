using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Exceptions;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using System.Net;

namespace Tubifarry.ImportLists.ListenBrainz.ListenBrainzCFRecommendations
{
    public class ListenBrainzCFRecommendationsImportList : HttpImportListBase<ListenBrainzCFRecommendationsSettings>
    {
        public override string Name => "ListenBrainz Recording Recommend";
        public override ImportListType ListType => ImportListType.Other;
        public override TimeSpan MinRefreshInterval => TimeSpan.FromDays(1);
        public override int PageSize => 0;
        public override TimeSpan RateLimit => TimeSpan.FromMilliseconds(200);

        public ListenBrainzCFRecommendationsImportList(IHttpClient httpClient,
                                           IImportListStatusService importListStatusService,
                                           IConfigService configService,
                                           IParsingService parsingService,
                                           Logger logger)
            : base(httpClient, importListStatusService, configService, parsingService, logger) { }

        public override IImportListRequestGenerator GetRequestGenerator() =>
            new ListenBrainzCFRecommendationsRequestGenerator(Settings);

        public override IParseImportListResponse GetParser() =>
            new ListenBrainzCFRecommendationsParser();

        protected override bool IsValidRelease(ImportListItemInfo release) =>
            release.AlbumMusicBrainzId.IsNotNullOrWhiteSpace() ||
            release.ArtistMusicBrainzId.IsNotNullOrWhiteSpace() ||
            (!release.Album.IsNullOrWhiteSpace() || !release.Artist.IsNullOrWhiteSpace());

        protected override void Test(List<ValidationFailure> failures) =>
            failures.AddIfNotNull(TestConnection());

        protected override ValidationFailure TestConnection()
        {
            try
            {
                ImportListRequest? firstRequest = GetRequestGenerator()
                    .GetListItems()
                    .GetAllTiers()
                    .FirstOrDefault()?
                    .FirstOrDefault();

                if (firstRequest == null)
                {
                    return new ValidationFailure(string.Empty, "No requests generated, check your configuration");
                }

                ImportListResponse response = FetchImportListResponse(firstRequest);

                if (response.HttpResponse.StatusCode == HttpStatusCode.NoContent)
                {
                    return new ValidationFailure(string.Empty, "No recording recommendations available for this user. These are generated based on collaborative filtering and may not be available for all users");
                }

                if (response.HttpResponse.StatusCode != HttpStatusCode.OK)
                {
                    return new ValidationFailure(string.Empty, $"Connection failed with HTTP {(int)response.HttpResponse.StatusCode} ({response.HttpResponse.StatusCode})");
                }
                return null!;
            }
            catch (ImportListException ex)
            {
                _logger.Warn(ex, "Connection test failed");
                return new ValidationFailure(string.Empty, $"Connection error: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Test connection failed");
                return new ValidationFailure(string.Empty, "Configuration error, check logs for details");
            }
        }
    }
}