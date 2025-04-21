using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;

namespace Tubifarry.Core.Replacements
{
    /// <summary>
    /// Generic indexer request generator interface that supports different request chain types
    /// </summary>
    public interface IIndexerRequestGenerator<TIndexerPageableRequest>
            where TIndexerPageableRequest : IndexerPageableRequest
    {
        /// <summary>
        /// Gets requests for recent releases
        /// </summary>
        IndexerPageableRequestChain<TIndexerPageableRequest> GetRecentRequests();

        /// <summary>
        /// Gets search requests for an album
        /// </summary>
        IndexerPageableRequestChain<TIndexerPageableRequest> GetSearchRequests(AlbumSearchCriteria searchCriteria);

        /// <summary>
        /// Gets search requests for an artist
        /// </summary>
        IndexerPageableRequestChain<TIndexerPageableRequest> GetSearchRequests(ArtistSearchCriteria searchCriteria);
    }
}