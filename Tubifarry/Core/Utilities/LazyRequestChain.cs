using NLog;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using System.Collections;

namespace Tubifarry.Core.Utilities
{
    /// <summary>
    /// Lazy version of IndexerPageableRequest that defers execution until enumerated
    /// </summary>
    public class LazyIndexerPageableRequest : IndexerPageableRequest
    {
        public int MinimumResultsThreshold { get; }

        public LazyIndexerPageableRequest(Func<IEnumerable<IndexerRequest>> requestFactory, int minimumResultsThreshold = 0)
            : base(new LazyEnumerable(requestFactory)) => MinimumResultsThreshold = minimumResultsThreshold;

        public bool AreResultsUsable(int resultsCount) => MinimumResultsThreshold == 0 ? resultsCount > 0 : resultsCount >= MinimumResultsThreshold;

        /// <summary>
        /// Helper class that wraps the lazy factory for the base constructor
        /// </summary>
        private class LazyEnumerable : IEnumerable<IndexerRequest>
        {
            private readonly Func<IEnumerable<IndexerRequest>> _factory;

            public LazyEnumerable(Func<IEnumerable<IndexerRequest>> factory) => _factory = factory;

            public IEnumerator<IndexerRequest> GetEnumerator() => _factory().GetEnumerator();

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }

    /// <summary>
    /// Generic version of IndexerPageableRequestChain with proper Add implementation
    /// </summary>
    public class IndexerPageableRequestChain<TRequest> where TRequest : IndexerPageableRequest
    {
        protected List<List<TRequest>> _chains;
        private static readonly Logger Logger = NzbDroneLogger.GetLogger(typeof(IndexerPageableRequestChain<TRequest>));

        public IndexerPageableRequestChain() => _chains = new List<List<TRequest>> { new() };

        public virtual int Tiers => _chains.Count;

        public virtual IEnumerable<TRequest> GetAllTiers() => _chains.SelectMany(v => v);

        public virtual IEnumerable<TRequest> GetTier(int index) => index < _chains.Count ? _chains[index] : Enumerable.Empty<TRequest>();

        public virtual void Add(IEnumerable<IndexerRequest> request)
        {
            if (request == null) return;

            if (typeof(TRequest) == typeof(LazyIndexerPageableRequest) && new LazyIndexerPageableRequest(() => request) is TRequest lazyRequest)
            {
                _chains[^1].Add(lazyRequest);
                Logger.Trace($"Added request to current tier. Current tier now has {_chains[^1].Count} requests.");
            }
            //else if (new ExtendedIndexerPageableRequest(request) is TRequest pageableRequest)
            //{
            //    _chains[^1].Add(pageableRequest);
            //}
        }

        public virtual void AddTier(IEnumerable<IndexerRequest> request)
        {
            AddTier();
            Add(request);
        }

        public virtual void AddTier()
        {
            if (_chains[^1].Count == 0) return;

            _chains.Add(new List<TRequest>());
            Logger.Trace($"Added new tier. Total tiers: {_chains.Count}");
        }

        /// <summary>
        /// Determines if results from the specified tier are usable based on tier-specific criteria
        /// </summary>
        public virtual bool AreTierResultsUsable(int tierIndex, int resultsCount) => resultsCount > 0;

        /// <summary>
        /// Converts to standard IndexerPageableRequestChain for compatibility
        /// </summary>
        public virtual IndexerPageableRequestChain ToStandardChain()
        {
            IndexerPageableRequestChain standardChain = new();

            if (_chains.Count > 0 && _chains[0].Count > 0)
            {
                foreach (TRequest request in _chains[0])
                    standardChain.Add(request);
            }

            for (int i = 1; i < _chains.Count; i++)
            {
                if (_chains[i].Count > 0)
                {
                    standardChain.AddTier();
                    foreach (TRequest request in _chains[i])
                        standardChain.Add(request);
                }
            }

            return standardChain;
        }
    }

    /// <summary>
    /// Lazy request chain that generates tiers on-demand
    /// </summary>
    public class LazyIndexerPageableRequestChain : IndexerPageableRequestChain<LazyIndexerPageableRequest>
    {
        private readonly int _defaultThreshold;
        private static readonly Logger Logger = NzbDroneLogger.GetLogger(typeof(LazyIndexerPageableRequestChain));

        public LazyIndexerPageableRequestChain(int defaultThreshold = 0)
        {
            _defaultThreshold = defaultThreshold;
        }

        /// <summary>
        /// Add a request factory to the current tier
        /// </summary>
        public void AddFactory(Func<IEnumerable<IndexerRequest>> requestFactory, int minimumResultsThreshold = 0)
        {
            int threshold = minimumResultsThreshold == 0 ? _defaultThreshold : minimumResultsThreshold;
            LazyIndexerPageableRequest lazyRequest = new(requestFactory, threshold);
            _chains[^1].Add(lazyRequest);
            Logger.Trace($"Added factory to current tier. Current tier now has {_chains[^1].Count} requests.");
        }

        /// <summary>
        /// Add a new tier with a request factory
        /// </summary>
        public void AddTierFactory(Func<IEnumerable<IndexerRequest>> requestFactory, int minimumResultsThreshold = 0)
        {
            AddTier();
            AddFactory(requestFactory, minimumResultsThreshold);
        }

        public override bool AreTierResultsUsable(int tierIndex, int resultsCount)
        {
            if (tierIndex >= _chains.Count) return false;

            int maxThreshold = 0;
            foreach (LazyIndexerPageableRequest request in _chains[tierIndex])
            {
                if (request.MinimumResultsThreshold > maxThreshold)
                    maxThreshold = request.MinimumResultsThreshold;
            }

            return maxThreshold == 0 ? resultsCount > 0 : resultsCount >= maxThreshold;
        }

        public override void Add(IEnumerable<IndexerRequest> request)
        {
            if (request != null)
                AddFactory(() => request);
        }

        public override void AddTier(IEnumerable<IndexerRequest> request)
        {
            if (request != null)
                AddTierFactory(() => request);
        }
    }

    /// <summary>
    /// Search tier generator that creates request factories instead of materialized requests
    /// </summary>
    public static class SearchTierGenerator
    {
        /// <summary>
        /// Creates a conditional tier that only executes if the condition is met
        /// </summary>
        public static Func<IEnumerable<IndexerRequest>> CreateConditionalTier(Func<bool> condition, Func<IEnumerable<IndexerRequest>> requestGenerator) => () => condition() ? requestGenerator() : Enumerable.Empty<IndexerRequest>();

        /// <summary>
        /// Creates a simple tier that always executes
        /// </summary>
        public static Func<IEnumerable<IndexerRequest>> CreateTier(Func<IEnumerable<IndexerRequest>> requestGenerator) => requestGenerator;
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