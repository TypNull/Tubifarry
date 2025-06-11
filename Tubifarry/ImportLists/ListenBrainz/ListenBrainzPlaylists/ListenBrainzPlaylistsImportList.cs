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

namespace Tubifarry.ImportLists.ListenBrainz.ListenBrainzPlaylists
{
    public class ListenBrainzPlaylistsImportList : HttpImportListBase<ListenBrainzPlaylistsSettings>
    {
        private readonly IHttpClient _httpClient;

        public override string Name => "ListenBrainz Created-For";
        public override ImportListType ListType => ImportListType.Other;
        public override TimeSpan MinRefreshInterval => TimeSpan.FromDays(1);
        public override int PageSize => 0; // No pagination
        public override TimeSpan RateLimit => TimeSpan.FromMilliseconds(200);

        public ListenBrainzPlaylistsImportList(IHttpClient httpClient,
                                   IImportListStatusService importListStatusService,
                                   IConfigService configService,
                                   IParsingService parsingService,
                                   Logger logger)
            : base(httpClient, importListStatusService, configService, parsingService, logger)
        {
            _httpClient = httpClient;
        }

        public override IImportListRequestGenerator GetRequestGenerator()
        {
            return new ListenBrainzPlaylistsRequestGenerator(Settings);
        }

        public override IParseImportListResponse GetParser()
        {
            ListenBrainzPlaylistsRequestGenerator requestGenerator = new(Settings);
            return new ListenBrainzPlaylistsParser(Settings, requestGenerator, _httpClient);
        }

        protected override void Test(List<ValidationFailure> failures)
        {
            failures.AddIfNotNull(TestConnection());
        }

        protected override ValidationFailure TestConnection()
        {
            try
            {
                IImportListRequestGenerator generator = GetRequestGenerator();
                ImportListPageableRequest requests = generator.GetListItems().GetAllTiers().First();

                if (!requests.Any())
                {
                    return new ValidationFailure(string.Empty, "No requests generated. Check your configuration.");
                }

                ImportListRequest firstRequest = requests.First();
                ImportListResponse response = FetchImportListResponse(firstRequest);

                if (response.HttpResponse.StatusCode != HttpStatusCode.OK)
                {
                    return new ValidationFailure(string.Empty,
                        $"Connection failed: HTTP {(int)response.HttpResponse.StatusCode} ({response.HttpResponse.StatusCode})");
                }

                IParseImportListResponse parser = GetParser();
                IList<ImportListItemInfo> items = parser.ParseResponse(response);

                _logger.Info($"Test successful, found {items.Count} items from created-for playlists");
                return null;
            }
            catch (ImportListException ex)
            {
                _logger.Warn(ex, "Connection test failed");
                return new ValidationFailure(string.Empty, $"Connection error: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Test connection failed");
                return new ValidationFailure(string.Empty, "Configuration error - check logs for details");
            }
        }
    }
}