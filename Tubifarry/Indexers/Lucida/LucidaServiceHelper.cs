using NLog;
using NzbDrone.Common.Http;
using Requests;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Indexers.Lucida
{
    /// <summary>
    /// Helper class to discover and cache available services from Lucida instances
    /// </summary>
    public static class LucidaServiceHelper
    {
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

        private static readonly ConcurrentDictionary<string, Task<Dictionary<string, List<ServiceCountry>>>> _cache
            = new(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, DateTime> _refreshAfter
            = new(StringComparer.OrdinalIgnoreCase);

        private static readonly TimeSpan EmptyResultCacheDuration = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan PartialResultCacheDuration = TimeSpan.FromMinutes(30);

        // Known services that Lucida supports
        private static readonly IReadOnlyDictionary<string, string> _knownServices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["qobuz"] = "Qobuz",
            ["tidal"] = "Tidal",
            ["soundcloud"] = "SoundCloud",
            ["deezer"] = "Deezer",
            ["amazon"] = "Amazon Music",
            ["yandex"] = "Yandex Music"
        };

        // Quality mapping for Lucida services
        public static readonly IReadOnlyDictionary<string, (AudioFormat Format, int Bitrate, int BitDepth)> ServiceQualityMap
            = new Dictionary<string, (AudioFormat, int, int)>(StringComparer.OrdinalIgnoreCase)
            {
                ["qobuz"] = (AudioFormat.FLAC, 1000, 16),
                ["tidal"] = (AudioFormat.FLAC, 1000, 16),
                ["deezer"] = (AudioFormat.MP3, 320, 0),
                ["soundcloud"] = (AudioFormat.AAC, 128, 0),
                ["amazon"] = (AudioFormat.FLAC, 1000, 8),
                ["yandex"] = (AudioFormat.MP3, 320, 0)
            };

        /// <summary>
        /// Gets services available for a specific Lucida instance
        /// </summary>
        public static Task<Dictionary<string, List<ServiceCountry>>> GetServicesAsync(
            string baseUrl,
            IHttpClient httpClient,
            Logger logger)
        {
            baseUrl = baseUrl.TrimEnd('/');
            Task<Dictionary<string, List<ServiceCountry>>> task = _cache.GetOrAdd(baseUrl, _ => FetchServicesAsync(baseUrl, httpClient, logger));
            if (task.IsCompleted && (!task.IsCompletedSuccessfully || (task.Result.Count < _knownServices.Count && DateTime.UtcNow >= _refreshAfter.GetValueOrDefault(baseUrl))))
            {
                _cache.TryRemove(new KeyValuePair<string, Task<Dictionary<string, List<ServiceCountry>>>>(baseUrl, task));
                task = _cache.GetOrAdd(baseUrl, _ => FetchServicesAsync(baseUrl, httpClient, logger));
            }
            return task;
        }

        public static bool IsFetchCompleted(string baseUrl) =>
            _cache.TryGetValue(baseUrl.TrimEnd('/'), out Task<Dictionary<string, List<ServiceCountry>>>? task) && task.IsCompleted;

        /// <summary>
        /// Check if services are available for a specific Lucida instance
        /// </summary>
        public static bool HasAvailableServices(string baseUrl)
        {
            string key = baseUrl.TrimEnd('/');
            if (_cache.TryGetValue(key, out Task<Dictionary<string, List<ServiceCountry>>>? task) && task.Status == TaskStatus.RanToCompletion)
            {
                return task.GetAwaiter().GetResult().Count != 0;
            }
            return false;
        }

        /// <summary>
        /// Get available services for a specific Lucida instance
        /// </summary>
        public static Dictionary<string, List<ServiceCountry>> GetAvailableServices(string baseUrl)
        {
            string key = baseUrl.TrimEnd('/');
            if (_cache.TryGetValue(key, out Task<Dictionary<string, List<ServiceCountry>>>? task) && task.Status == TaskStatus.RanToCompletion)
                return task.GetAwaiter().GetResult();
            return new Dictionary<string, List<ServiceCountry>>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Gets the display name for a service value
        /// </summary>
        public static string GetServiceDisplayName(string serviceValue)
            => _knownServices.TryGetValue(serviceValue, out string? display)
                ? display
                : char.ToUpperInvariant(serviceValue[0]) + serviceValue[1..];

        /// <summary>
        /// Gets the service key for a display name
        /// </summary>
        public static string? GetServiceKey(string displayName)
            => _knownServices.FirstOrDefault(kvp => string.Equals(kvp.Value, displayName, StringComparison.OrdinalIgnoreCase)).Key;

        /// <summary>
        /// Gets the quality information for a service
        /// </summary>
        public static (AudioFormat Format, int Bitrate, int BitDepth) GetServiceQuality(string serviceValue) =>
            ServiceQualityMap.TryGetValue(serviceValue, out (AudioFormat Format, int Bitrate, int BitDepth) quality)
                ? quality : (AudioFormat.MP3, 320, 0);

        /// <summary>
        /// Clear the cached services for a specific instance
        /// </summary>
        public static void ClearCache(string baseUrl) => _cache.TryRemove(baseUrl.TrimEnd('/'), out _);

        /// <summary>
        /// Clear all cached services
        /// </summary>
        public static void ClearAllCaches() => _cache.Clear();

        private static async Task<Dictionary<string, List<ServiceCountry>>> FetchServicesAsync(string baseUrl, IHttpClient httpClient, Logger logger)
        {
            Dictionary<string, List<ServiceCountry>> result = new(StringComparer.OrdinalIgnoreCase);
            RequestContainer<OwnRequest> container = [];
            foreach (string service in _knownServices.Keys)
            {
                container.Add(new OwnRequest(async _ =>
                {
                    string url = $"{baseUrl}/api/load?url=%2Fapi%2Fcountries%3Fservice%3D{service}";
                    logger.Trace("Fetching countries for service {Service}: {Url}", service, url);

                    for (int attempt = 0; attempt < LucidaRetryPolicy.MaxAttempts; attempt++)
                    {
                        if (attempt > 0)
                            await LucidaRetryPolicy.DelayAsync(attempt);

                        try
                        {
                            HttpRequest req = new(url);
                            req.Headers["User-Agent"] = Tubifarry.UserAgent;
                            HttpResponse response = await httpClient.ExecuteAsync(req);
                            if (response.StatusCode != HttpStatusCode.OK)
                            {
                                logger.Warn("Failed to get countries for service {Service}: {StatusCode}", service, response.StatusCode);
                                continue;
                            }

                            CountryResponse? payload = JsonSerializer.Deserialize<CountryResponse>(response.Content, _jsonOptions);
                            if (payload?.Success == true && payload.Countries?.Count > 0)
                            {
                                lock (result)
                                    result[service] = payload.Countries;
                                logger.Trace("Found {Count} countries for service {Service}", payload.Countries.Count, service);
                                break;
                            }
                        }
                        catch (Exception ex) when (!LucidaRetryPolicy.IsTransient(ex))
                        {
                            logger.Error(ex, "Error fetching countries for service {Service}", service);
                            break;
                        }
                        catch (Exception ex) when (!LucidaRetryPolicy.IsLastAttempt(attempt))
                        {
                            logger.Debug(ex, "Error fetching countries for service {Service}, retrying ({Attempt}/{MaxRetries})", service, attempt + 1, LucidaRetryPolicy.MaxAttempts - 1);
                        }
                        catch (Exception ex)
                        {
                            logger.Error(ex, "Error fetching countries for service {Service}", service);
                        }
                    }
                    return true;
                }));
            }
            await container.Task;
            if (result.Count < _knownServices.Count)
                _refreshAfter[baseUrl] = DateTime.UtcNow.Add(result.Count == 0 ? EmptyResultCacheDuration : PartialResultCacheDuration);
            return result;
        }
    }
}