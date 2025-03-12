using NLog;
using NzbDrone.Common.Instrumentation;
using System.Diagnostics;
using Tubifarry.Metadata.Proxy.Core;

namespace Tubifarry.Metadata.Proxy.Mixed
{
    public class ProxyDecisionHandler<TResult>
    {
        private readonly MixedMetadataProxy _mixedProxy;
        private readonly Func<IProxy, List<TResult>> _searchExecutor;
        private readonly Func<List<TResult>, TResult, bool> _containsItem;
        private readonly Func<bool> _isValidQuery;
        private readonly Func<ISupportMetadataMixing, MetadataSupportLevel> _supportSelector;
        private readonly Logger _logger;

        public ProxyDecisionHandler(MixedMetadataProxy mixedProxy, Func<IProxy, List<TResult>> searchExecutor, Func<List<TResult>, TResult, bool> containsItem, Func<bool>? isValidQuery, Func<ISupportMetadataMixing, MetadataSupportLevel> supportSelector)
        {
            _mixedProxy = mixedProxy;
            _searchExecutor = searchExecutor;
            _containsItem = containsItem;
            _isValidQuery = isValidQuery ?? (() => true);
            _supportSelector = supportSelector;
            _logger = NzbDroneLogger.GetLogger(this);
        }

        public List<TResult> ExecuteSearch()
        {
            List<TResult> aggregatedItems = new();
            int bestPriority = int.MaxValue;

            foreach (ProxyCandidate candidate in _mixedProxy.GetCandidateProxies(_supportSelector))
            {
                if (bestPriority == int.MaxValue)
                {
                    bestPriority = candidate.Priority;
                }
                else
                {
                    int threshold = _mixedProxy.CalculateThreshold(candidate.Proxy.Definition.Name, aggregatedItems.Count);
                    if (candidate.Priority > bestPriority + threshold)
                    {
                        _logger.Debug($"Stopping aggregation due to threshold. Candidate proxy {candidate.Proxy.Definition.Name} with priority {candidate.Priority} exceeds threshold (threshold={threshold}).");
                        break;
                    }
                }

                Stopwatch sw = Stopwatch.StartNew();
                List<TResult> items = _searchExecutor(candidate.Proxy);
                sw.Stop();

                List<TResult> newItems = items.Where(item => !_containsItem(aggregatedItems, item)).ToList();

                aggregatedItems.AddRange(newItems);

                int newCount = newItems.Count;
                _logger.Trace($"{candidate.Proxy.Definition.Name} returned {items.Count} items, {newCount} new.");

                bool queryValid = _isValidQuery();
                bool success = !queryValid || newCount > 0;
                _mixedProxy._adaptiveThreshold.UpdateMetrics(candidate.Proxy.Definition.Name, sw.Elapsed.TotalMilliseconds, newCount, success);

                if (newCount == 0 && aggregatedItems.Any())
                {
                    _logger.Trace($"No new items from proxy {candidate.Proxy.Definition.Name}, stopping further calls.");
                    break;
                }
            }
            return aggregatedItems;
        }
    }

    internal record ProxyCandidate
    {
        public IProxy Proxy { get; set; } = null!;
        public int Priority { get; set; }
        public MetadataSupportLevel Support { get; set; }
    }
}
