using NLog;
using NzbDrone.Common.Instrumentation;
using System.Text.Json;

namespace Tubifarry.Metadata.Proxy.MetadataProvider.Mixed
{
    public interface IProvideAdaptiveThreshold
    {
        public void LoadConfig(string? configPath);

        public int GetDynamicThreshold(string proxyName, int aggregatedCount);

        public void UpdateMetrics(string proxyName, double responseTimeMs, int newCount, bool success);
    }

    public class AdaptiveThresholdManager : IProvideAdaptiveThreshold, IDisposable
    {
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
        private static readonly Logger _logger = NzbDroneLogger.GetLogger(typeof(AdaptiveThresholdManager));
        private const string DefaultFileName = "adaptive_weights.json";

        private string? _requestedPath;
        private string? _configFilePath;
        private bool _saveFailureLogged;
        private bool _disposed;
        private System.Timers.Timer? _saveTimer;
        private bool _isDirty;
        private bool _processExitRegistered;
        private readonly object _lock = new();

        public AdaptiveThresholdConfig Config { get; private set; } = new();

        public void LoadConfig(string? configPath)
        {
            lock (_lock)
            {
                if (_disposed)
                    return;

                string? requestedPath = string.IsNullOrWhiteSpace(configPath) ? null : configPath;
                if (requestedPath == _requestedPath)
                    return;

                _requestedPath = requestedPath;
                string? newPath = requestedPath == null ? null : ResolveConfigPath(requestedPath);
                if (newPath == _configFilePath)
                    return;

                SaveConfig();
                _saveTimer?.Dispose();
                _saveTimer = null;
                _configFilePath = newPath;
                _saveFailureLogged = false;
                Config = new AdaptiveThresholdConfig();
                _isDirty = false;

                if (_configFilePath == null)
                    return;

                if (File.Exists(_configFilePath))
                {
                    try
                    {
                        Config = JsonSerializer.Deserialize<AdaptiveThresholdConfig>(File.ReadAllText(_configFilePath)) ?? new AdaptiveThresholdConfig();
                        _isDirty = false;
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, $"Failed to load adaptive threshold config from {_configFilePath}, keeping a backup at {_configFilePath}.bak");
                        try
                        {
                            File.Copy(_configFilePath, _configFilePath + ".bak", true);
                        }
                        catch (Exception copyEx)
                        {
                            _logger.Debug(copyEx, $"Failed to back up adaptive threshold config {_configFilePath}");
                        }
                    }
                }

                _saveTimer = new System.Timers.Timer(300000) { AutoReset = true };
                _saveTimer.Elapsed += (s, e) => SaveConfig();
                _saveTimer.Start();

                if (!_processExitRegistered)
                {
                    AppDomain.CurrentDomain.ProcessExit += (s, e) => SaveConfig();
                    _processExitRegistered = true;
                }
            }
        }

        private void SaveConfig()
        {
            lock (_lock)
            {
                if (!_isDirty || _configFilePath == null) return;
                try
                {
                    File.WriteAllText(_configFilePath, JsonSerializer.Serialize(Config, _jsonOptions));
                    _isDirty = false;
                }
                catch (Exception ex)
                {
                    if (_saveFailureLogged)
                    {
                        _logger.Debug(ex, $"Failed to save adaptive threshold config to {_configFilePath}");
                    }
                    else
                    {
                        _logger.Warn(ex, $"Failed to save adaptive threshold config to {_configFilePath}");
                        _saveFailureLogged = true;
                    }
                }
            }
        }

        private static string ResolveConfigPath(string path) =>
            Directory.Exists(path) || path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
                ? Path.Combine(path, DefaultFileName)
                : path;

        /// <summary>
        /// Applies exponential decay to the stored metrics based on the time elapsed since the last update.
        /// </summary>
        /// <param name="metrics">The proxy metrics to decay.</param>
        private void DecayMetrics(ProxyMetrics metrics)
        {
            double hoursElapsed = (DateTime.UtcNow - metrics.LastUpdate).TotalHours;
            if (hoursElapsed > 0)
            {
                double decayFactor = Math.Exp(-Config.DecayRatePerHour * hoursElapsed);
                metrics.Calls *= decayFactor;
                metrics.Failures *= decayFactor;
                metrics.TotalResponseTime *= decayFactor;
                metrics.TotalNewResults *= decayFactor;
                metrics.LastUpdate = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Computes a dynamic threshold for a given proxy based on its historical metrics and the aggregated result count.
        /// </summary>
        /// <param name="proxyName">The name of the proxy.</param>
        /// <param name="aggregatedCount">The aggregated result count.</param>
        /// <returns>The computed dynamic threshold.</returns>
        public int GetDynamicThreshold(string proxyName, int aggregatedCount)
        {
            lock (_lock)
            {
                int baseThreshold = aggregatedCount < 10 ? Config.BaseThresholdLowCount : aggregatedCount < 50 ? Config.BaseThresholdMediumCount : Config.BaseThresholdHighCount;

                if (!Config.ProxyMetrics.TryGetValue(proxyName, out ProxyMetrics? metrics) || metrics.Calls < Config.MinCallsForReliableMetrics)
                    return baseThreshold;

                DecayMetrics(metrics);

                double failureRatio = metrics.Calls > 0 ? metrics.Failures / metrics.Calls : 0.0;
                double qualityAdjustment = 1.0 - metrics.QualityScore;
                double performanceAdjustment = 1.0 - metrics.PerformanceScore;

                // Weights can be tuned; here we use a weighted sum.
                double adjustment = (0.5 * qualityAdjustment) + (0.3 * performanceAdjustment) + (0.2 * failureRatio);
                int maxAdjustment = Config.MaxThresholdCount - baseThreshold;
                int dynamicThreshold = baseThreshold + (int)Math.Round(adjustment * maxAdjustment);
                return dynamicThreshold < 1 ? 1 : dynamicThreshold;
            }
        }

        /// <summary>
        /// Updates the metrics for a given proxy based on the latest call.
        /// </summary>
        /// <param name="proxyName">The name of the proxy.</param>
        /// <param name="responseTimeMs">The response time (in milliseconds) for the call.</param>
        /// <param name="newCount">The number of new results returned by the call.</param>
        /// <param name="success">Whether the call was considered a valid success.</param>
        public void UpdateMetrics(string proxyName, double responseTimeMs, int newCount, bool success)
        {
            lock (_lock)
            {
                if (!Config.ProxyMetrics.TryGetValue(proxyName, out ProxyMetrics? metrics))
                {
                    metrics = new ProxyMetrics();
                    Config.ProxyMetrics[proxyName] = metrics;
                }

                DecayMetrics(metrics);

                metrics.Calls++;
                metrics.TotalResponseTime += responseTimeMs;
                metrics.TotalNewResults += newCount;
                if (!success) metrics.Failures++;

                double learningRate = Config.InitialLearningRate / (1 + (Config.LearningRateDecay * metrics.Calls));

                double expectedCount = metrics.ExpectedNewResults;
                double measuredQuality = expectedCount > 0 ? Math.Min(1.0, newCount / expectedCount) : 0.0;
                metrics.QualityScore += learningRate * (measuredQuality - metrics.QualityScore);

                double measuredPerformance = 1.0;
                if (responseTimeMs > 0)
                {
                    double resultsPerSecond = newCount / (responseTimeMs / 1000.0);
                    double expectedRate = metrics.AverageResponseTime > 0 ? metrics.ExpectedNewResults / (metrics.AverageResponseTime / 1000.0) : 1.0;
                    if (expectedRate > 0)
                        measuredPerformance = Math.Min(1.0, resultsPerSecond / expectedRate);
                }
                metrics.PerformanceScore += learningRate * (measuredPerformance - metrics.PerformanceScore);
                _isDirty = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            lock (_lock)
            {
                if (_disposed)
                    return;

                if (disposing)
                {
                    SaveConfig();
                    _saveTimer?.Dispose();
                    _saveTimer = null;
                }
                _disposed = true;
            }
        }

        ~AdaptiveThresholdManager() => Dispose(false);
    }
}