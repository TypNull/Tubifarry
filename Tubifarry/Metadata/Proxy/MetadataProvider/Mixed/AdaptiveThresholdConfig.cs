namespace Tubifarry.Metadata.Proxy.MetadataProvider.Mixed
{
    public class AdaptiveThresholdConfig
    {
        /// <summary>
        /// Represents the base threshold for proxies with a low aggregated result count.
        /// </summary>
        public int BaseThresholdLowCount { get; set; } = 5;

        /// <summary>
        /// Represents the base threshold for proxies with a medium aggregated result count.
        /// </summary>
        public int BaseThresholdMediumCount { get; set; } = 3;

        /// <summary>
        /// Represents the base threshold for proxies with a high aggregated result count.
        /// </summary>
        public int BaseThresholdHighCount { get; set; } = 1;

        /// <summary>
        /// Represents the maximal possible threshold for proxies.
        /// </summary>
        public int MaxThresholdCount { get; set; } = 15;

        /// <summary>
        /// Represents the initial learning rate used for updating proxy metrics.
        /// </summary>
        public double InitialLearningRate { get; set; } = 0.2;

        /// <summary>
        /// Represents the decay factor applied to the learning rate over time.
        /// </summary>
        public double LearningRateDecay { get; set; } = 0.01;

        /// <summary>
        /// Represents the minimum number of calls required before metrics are considered reliable.
        /// </summary>
        public int MinCallsForReliableMetrics { get; set; } = 5;

        /// <summary>
        /// Represents the exponential decay rate (per hour) applied to older metrics.
        /// </summary>
        public double DecayRatePerHour { get; set; } = 0.01;

        /// <summary>
        /// Stores metrics for each proxy, indexed by the proxy's name.
        /// </summary>
        public Dictionary<string, ProxyMetrics> ProxyMetrics { get; set; } = new Dictionary<string, ProxyMetrics>();
    }

}
