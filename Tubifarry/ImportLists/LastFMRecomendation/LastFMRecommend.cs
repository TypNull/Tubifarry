using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Parser;
using Tubifarry.ImportLists.LastFmRecomendation;

namespace Tubifarry.ImportLists.LastFmRecommend
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
    }
}
