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

namespace Tubifarry.ImportLists.ListenBrainz.ListenBrainzCreatedForPlaylist
{
    public class ListenBrainzCreatedForPlaylistImportList : HttpImportListBase<ListenBrainzCreatedForPlaylistSettings>
    {
        public override string Name => "ListenBrainz created for you";
        public override ImportListType ListType => ImportListType.Other;
        public override TimeSpan MinRefreshInterval => TimeSpan.FromDays(1);
        public override int PageSize => 0;
        public override TimeSpan RateLimit => TimeSpan.FromMilliseconds(200);

        public ListenBrainzCreatedForPlaylistImportList(IHttpClient httpClient,
                                   IImportListStatusService importListStatusService,
                                   IConfigService configService,
                                   IParsingService parsingService,
                                   Logger logger)
            : base(httpClient, importListStatusService, configService, parsingService, logger) { }

        public override IImportListRequestGenerator GetRequestGenerator() =>
            new ListenBrainzCreatedForPlaylistRequestGenerator(Settings);

        public override IParseImportListResponse GetParser() =>
            new ListenBrainzCreatedForPlaylistParser(Settings, new ListenBrainzCreatedForPlaylistRequestGenerator(Settings), _httpClient);

        protected override bool IsValidRelease(ImportListItemInfo release) =>
            release.AlbumMusicBrainzId.IsNotNullOrWhiteSpace() ||
            release.ArtistMusicBrainzId.IsNotNullOrWhiteSpace() ||
            !release.Album.IsNullOrWhiteSpace() || !release.Artist.IsNullOrWhiteSpace();

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

                if (response.HttpResponse.StatusCode != HttpStatusCode.OK)
                {
                    return new ValidationFailure(string.Empty, $"Connection failed with HTTP {(int)response.HttpResponse.StatusCode} ({response.HttpResponse.StatusCode})");
                }

                IList<ImportListItemInfo> items = GetParser().ParseResponse(response);
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
