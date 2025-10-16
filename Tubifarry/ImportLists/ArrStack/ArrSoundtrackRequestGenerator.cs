using NzbDrone.Common.Http;
using NzbDrone.Core.ImportLists;

namespace Tubifarry.ImportLists.ArrStack
{
    /// <summary>
    /// Generates HTTP requests for fetching media items from Arr applications.
    /// </summary>
    internal class ArrSoundtrackRequestGenerator(ArrSoundtrackImportSettings settings) : IImportListRequestGenerator
    {
        private readonly ArrSoundtrackImportSettings _settings = settings;

        public ImportListPageableRequestChain GetListItems()
        {
            ImportListPageableRequestChain chain = new();
            chain.AddTier(GetPagedRequests());
            return chain;
        }

        private IEnumerable<ImportListRequest> GetPagedRequests()
        {
            string url = _settings.BaseUrl.TrimEnd('/') + _settings.APIItemEndpoint;
            string urlWithAuth = $"{url}?apikey={_settings.ApiKey}&excludeLocalCovers=true";
            yield return new ImportListRequest(urlWithAuth, HttpAccept.Json);
        }
    }
}
