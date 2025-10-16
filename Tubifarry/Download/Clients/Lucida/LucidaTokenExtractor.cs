using NLog;
using NzbDrone.Common.Instrumentation;
using System.Text.RegularExpressions;
using Tubifarry.Download.Base;
using Tubifarry.Indexers.Lucida;

namespace Tubifarry.Download.Clients.Lucida
{
    /// <summary>
    /// Extracts authentication tokens from Lucida web pages
    /// Simple, focused implementation that gets the job done
    /// </summary>
    public static partial class LucidaTokenExtractor
    {
        private static readonly Logger _logger = NzbDroneLogger.GetLogger(typeof(LucidaTokenExtractor));

        /// <summary>
        /// Extracts tokens from a Lucida web page
        /// </summary>
        public static async Task<LucidaTokens> ExtractTokensAsync(BaseHttpClient httpClient, string url)
        {
            try
            {
                string lucidaUrl = $"{httpClient.BaseUrl}/?url={Uri.EscapeDataString(url)}";
                string html = await httpClient.GetStringAsync(lucidaUrl);
                return ExtractTokensFromHtml(html);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Token extraction failed for {0}", url);
                return LucidaTokens.Empty;
            }
        }

        /// <summary>
        /// Extracts tokens from HTML content
        /// </summary>
        public static LucidaTokens ExtractTokensFromHtml(string html)
        {
            if (string.IsNullOrEmpty(html))
                return LucidaTokens.Empty;

            try
            {
                string token = ExtractToken(html);
                long expiry = ExtractTokenExpiry(html);

                if (string.IsNullOrEmpty(token))
                {
                    _logger.Debug("No token found in HTML content");
                    return LucidaTokens.Empty;
                }
                return new LucidaTokens(token, token, expiry);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error extracting tokens from HTML");
                return LucidaTokens.Empty;
            }
        }

        /// <summary>
        /// Extracts the token using regex
        /// </summary>
        private static string ExtractToken(string html)
        {
            Match match = TokenRegex().Match(html);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        /// <summary>
        /// Extracts token expiry timestamp
        /// </summary>
        private static long ExtractTokenExpiry(string html)
        {
            Match match = TokenExpiryRegex().Match(html);
            if (match.Success && long.TryParse(match.Groups[1].Value, out long expiry))
                return expiry;
            return DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
        }

        [GeneratedRegex(@"""token""\s*:\s*""([^""]+)""", RegexOptions.Compiled)]
        private static partial Regex TokenRegex();
        [GeneratedRegex(@"""tokenExpiry""\s*:\s*(\d+)", RegexOptions.Compiled)]
        private static partial Regex TokenExpiryRegex();
    }
}
