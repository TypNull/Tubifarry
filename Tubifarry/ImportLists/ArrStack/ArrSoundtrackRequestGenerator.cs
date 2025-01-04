using NLog;
using NzbDrone.Common.Http;

namespace NzbDrone.Core.ImportLists.ArrStack
{
    internal class ArrSoundtrackRequestGenerator : IImportListRequestGenerator
    {
        public ArrSoundtrackImportSettings Settings { get; set; }
        public Logger Logger { get; set; }

        public ArrSoundtrackRequestGenerator(ArrSoundtrackImportSettings settings, Logger logger)
        {
            Settings = settings;
            Logger = logger;
        }

        public ImportListPageableRequestChain GetListItems()
        {
            ImportListPageableRequestChain chain = new();
            chain.AddTier(GetPagedRequests());
            return chain;
        }

        private IEnumerable<ImportListRequest> GetPagedRequests()
        {
            yield return new ImportListRequest($"{Settings.BaseUrl}{Settings.APIItemEndpoint}?apikey={Settings.ApiKey}&excludeLocalCovers=true", HttpAccept.Json);
        }
    }
}
