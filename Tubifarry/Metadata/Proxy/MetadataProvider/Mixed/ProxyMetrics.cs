namespace Tubifarry.Metadata.Proxy.MetadataProvider.Mixed
{
    public class ProxyMetrics
    {
        /// <summary>
        /// Represents the quality score of the proxy on a scale from 0 to 1, where 1.0 is the best.
        /// </summary>
        public double QualityScore { get; set; } = 1.0;

        /// <summary>
        /// Represents the performance score of the proxy on a scale from 0 to 1, where 1.0 is the best.
        /// </summary>
        public double PerformanceScore { get; set; } = 1.0;

        /// <summary>
        /// Represents the total number of calls made to the proxy.
        /// </summary>
        public double Calls { get; set; }

        /// <summary>
        /// Represents the total number of failed calls made to the proxy.
        /// </summary>
        public double Failures { get; set; }

        /// <summary>
        /// Represents the total response time (in milliseconds) for all calls made to the proxy.
        /// </summary>
        public double TotalResponseTime { get; set; }

        /// <summary>
        /// Represents the total number of new results returned by the proxy.
        /// </summary>
        public double TotalNewResults { get; set; }

        /// <summary>
        /// Represents the timestamp of the last update to the metrics, used for decaying old metrics.
        /// </summary>
        public DateTime LastUpdate { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Calculates the average response time (in milliseconds) for calls made to the proxy.
        /// </summary>
        public double AverageResponseTime => Calls > 0 ? TotalResponseTime / Calls : 0.0;

        /// <summary>
        /// Calculates the expected number of new results per call based on historical data.
        /// </summary>
        public double ExpectedNewResults => Calls > 0 ? TotalNewResults / Calls : 3.0;
    }
}
