using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;

namespace Tubifarry.Core.Utilities
{
    /// <summary>
    /// Extended version of IndexerPageableRequest that supports a minimum results threshold
    /// </summary>
    public class ExtendedIndexerPageableRequest : IndexerPageableRequest
    {
        /// <summary>
        /// Minimum number of results this tier should return before stopping the search
        /// If fewer results are found, the next tier will be tried
        /// </summary>
        public int MinimumResultsThreshold { get; set; }

        public ExtendedIndexerPageableRequest(IEnumerable<IndexerRequest> enumerable, int minimumResultsThreshold = 0)
            : base(enumerable) => MinimumResultsThreshold = minimumResultsThreshold;


        /// <summary>
        /// Determines if the number of results meets the minimum threshold requirement
        /// </summary>
        public bool AreResultsUsable(int resultsCount)
        {
            if (MinimumResultsThreshold == 0)
                return resultsCount > 0;
            return resultsCount >= MinimumResultsThreshold;
        }
    }

    /// <summary>
    /// Generic version of IndexerPageableRequestChain
    /// </summary>
    public class IndexerPageableRequestChain<TRequest> where TRequest : IndexerPageableRequest
    {
        protected List<List<TRequest>> _chains;

        public IndexerPageableRequestChain() => _chains = new List<List<TRequest>> { new() };

        public int Tiers => _chains.Count;

        public IEnumerable<TRequest> GetAllTiers() => _chains.SelectMany(v => v);

        public IEnumerable<TRequest> GetTier(int index) => _chains[index];

        public virtual void Add(IEnumerable<IndexerRequest> request)
        {
            if (request == null)
                return;
        }

        public virtual void AddTier(IEnumerable<IndexerRequest> request)
        {
            AddTier();
            Add(request);
        }

        public void AddTier()
        {
            if (_chains[^1].Count == 0)
                return;
            _chains.Add(new List<TRequest>());
        }

        /// <summary>
        /// Determines if results from the specified tier are usable based on tier-specific criteria
        /// </summary>
        public virtual bool AreTierResultsUsable(int tierIndex, int resultsCount) => resultsCount > 0;


        /// <summary>
        /// Converts to standard IndexerPageableRequestChain for compatibility
        /// </summary>
        public IndexerPageableRequestChain ToStandardChain()
        {
            IndexerPageableRequestChain standardChain = new();

            if (_chains.Count > 0 && _chains[0].Count > 0)
            {
                foreach (TRequest request in _chains[0])
                {
                    standardChain.Add(request);
                }
            }

            for (int i = 1; i < _chains.Count; i++)
            {
                if (_chains[i].Count > 0)
                {
                    standardChain.AddTier();
                    foreach (TRequest request in _chains[i])
                    {
                        standardChain.Add(request);
                    }
                }
            }

            return standardChain;
        }
    }

    /// <summary>
    /// Extended version of IndexerPageableRequestChain that supports minimum results threshold
    /// </summary>
    public class ExtendedIndexerPageableRequestChain : IndexerPageableRequestChain<ExtendedIndexerPageableRequest>
    {
        private readonly int _defaultThreshold;

        public ExtendedIndexerPageableRequestChain(int defaultThreshold = 0)
        {
            _defaultThreshold = defaultThreshold;
        }

        /// <summary>
        /// Adds a new request with the default threshold
        /// </summary>
        public override void Add(IEnumerable<IndexerRequest> request)
        {
            if (request == null)
                return;
            _chains[^1].Add(new ExtendedIndexerPageableRequest(request, _defaultThreshold));
        }

        /// <summary>
        /// Adds a new request with a specific threshold
        /// </summary>
        public void Add(IEnumerable<IndexerRequest> request, int minimumResultsThreshold)
        {
            if (request == null)
                return;
            _chains[^1].Add(new ExtendedIndexerPageableRequest(request, minimumResultsThreshold));
        }

        /// <summary>
        /// Adds a new tier with the default threshold
        /// </summary>
        public override void AddTier(IEnumerable<IndexerRequest> request)
        {
            AddTier();
            Add(request);
        }

        /// <summary>
        /// Adds a new tier with a specific threshold
        /// </summary>
        public void AddTier(IEnumerable<IndexerRequest> request, int minimumResultsThreshold)
        {
            AddTier();
            Add(request, minimumResultsThreshold);
        }

        /// <summary>
        /// Checks if the results for a tier meet the requirements
        /// </summary>
        public override bool AreTierResultsUsable(int tierIndex, int resultsCount)
        {
            int tierThreshold = 0;
            foreach (ExtendedIndexerPageableRequest request in GetTier(tierIndex))
            {
                if (request.MinimumResultsThreshold > tierThreshold)
                    tierThreshold = request.MinimumResultsThreshold;
            }

            if (tierThreshold == 0)
                tierThreshold = _defaultThreshold;

            if (tierThreshold == 0)
                return resultsCount > 0;

            return resultsCount >= tierThreshold;
        }
    }

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