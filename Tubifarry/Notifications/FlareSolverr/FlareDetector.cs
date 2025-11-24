using NLog;
using NzbDrone.Common.Instrumentation;
using System.Net;

namespace Tubifarry.Notifications.FlareSolverr;

/// <summary>
/// Detects Cloudflare and DDoS-GUARD protection challenges in HTTP responses
/// </summary>
public static class FlareDetector
{
    private static readonly Logger Logger = NzbDroneLogger.GetLogger(typeof(FlareDetector));

    private static readonly string[] CloudflareServerNames = ["cloudflare", "cloudflare-nginx", "ddos-guard"];

    private static readonly string[] CloudflareCookiePrefixes = ["cf_", "__cf", "__ddg"];

    private static readonly string[] CloudflareChallengeIndicators =
    [
        "<title>Just a moment...</title>",
        "<title>Checking your browser</title>",
        "jschl-answer",  // JavaScript challenge form field
        "cf-challenge",  // Cloudflare challenge class
        "cf-chl-bypass",  // Challenge bypass indicator
        "Verify you are human",  // Interactive challenge button
        "<title>DDOS-GUARD</title>"
    ];

    private static readonly string[] CloudflareBlockIndicators =
    [
        "<h1>Sorry, you have been blocked</h1>",
        "<h1 data-translate=\"block_headline\">Sorry, you have been blocked</h1>",
        "You are unable to access",
        "This website is using a security service to protect itself"
    ];

    private static readonly HttpStatusCode[] ProtectionStatusCodes =
    [
        HttpStatusCode.ServiceUnavailable, // 503
        HttpStatusCode.Forbidden,          // 403
        (HttpStatusCode)523                // Cloudflare: Origin Unreachable
    ];

    /// <summary>
    /// Checks if the HTTP response indicates Cloudflare protection is active
    /// </summary>
    public static bool IsChallengePresent(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        string url = response.RequestMessage?.RequestUri?.ToString() ?? "(unknown)";

        // Check status codes, successful responses (200-299) are never challenges
        if (response.IsSuccessStatusCode)
        {
            Logger.Trace("Status code {0} is successful, no challenge for {1}", response.StatusCode, url);
            return false;
        }

        // Check status codes that indicate protection challenges
        if (!ProtectionStatusCodes.Contains(response.StatusCode))
        {
            Logger.Trace("Status code {0} not a protection status code for {1}", response.StatusCode, url);
            return false;
        }

        // Check if Server header indicates Cloudflare/DDoS-GUARD
        bool isCloudflareServer = response.Headers.Server.Any(server =>
            server.Product != null &&
            CloudflareServerNames.Contains(server.Product.Name.ToLowerInvariant()));

        if (isCloudflareServer)
        {
            string? serverName = response.Headers.Server.First(s => s.Product != null).Product?.Name;
            Logger.Trace("Cloudflare/DDoS-Guard server detected ({0}) for {1}", serverName, url);
        }
        else
        {
            Logger.Trace("Not a Cloudflare/DDoS-Guard server for {0}", url);
            return false;
        }

        string responseText = response.Content.ReadAsStringAsync().Result;
        string? blockIndicator = CloudflareBlockIndicators.FirstOrDefault(indicator =>
            responseText.Contains(indicator, StringComparison.OrdinalIgnoreCase));

        if (blockIndicator != null)
        {
            Logger.Trace("Cloudflare block detected (not a challenge) for {0}. Indicator: '{1}'. FlareSolverr cannot bypass IP/region blocks.", url, blockIndicator.Substring(0, Math.Min(50, blockIndicator.Length)));
            return false;
        }

        // Check for Cloudflare error codes
        if (responseText.TrimStart().StartsWith("error code:", StringComparison.OrdinalIgnoreCase))
        {
            Logger.Trace("Cloudflare error code detected in content for {0}", url);
            return true;
        }

        // Check for actual challenge indicators
        string? challengeIndicator = CloudflareChallengeIndicators.FirstOrDefault(indicator =>
            responseText.Contains(indicator, StringComparison.OrdinalIgnoreCase));

        if (challengeIndicator != null)
        {
            Logger.Trace("Challenge indicator '{0}' found in content for {1}", challengeIndicator, url);
            return true;
        }

        // Check for custom Cloudflare configurations (some Dutch torrent sites)
        if (response.Headers.Vary.ToString() == "Accept-Encoding,User-Agent" &&
            string.IsNullOrEmpty(response.Content.Headers.ContentEncoding.ToString()) &&
            responseText.Contains("ddos", StringComparison.OrdinalIgnoreCase))
        {
            Logger.Trace("Custom DDoS protection pattern detected for {0}", url);
            return true;
        }

        Logger.Trace("No protection challenge detected for {0}", url);
        return false;
    }

    /// <summary>
    /// Checks if a cookie name is a Cloudflare/DDoS-GUARD cookie
    /// </summary>
    public static bool IsProtectionCookie(string cookieName) =>
        CloudflareCookiePrefixes.Any(prefix =>
            cookieName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
