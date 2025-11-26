using System.Net;

namespace Tubifarry.Core.Records
{
    /// <summary>
    /// Represents session token data for transportation and caching
    /// </summary>
    public record SessionTokens(
        string PoToken,
        string VisitorData,
        DateTime ExpiryUtc,
        string Source = "Unknown")
    {
        /// <summary>
        /// Checks if the tokens are still valid (not expired)
        /// </summary>
        public bool IsValid => !IsEmpty && DateTime.UtcNow < ExpiryUtc;

        /// <summary>
        /// Checks if the tokens are empty
        /// </summary>
        public bool IsEmpty => string.IsNullOrEmpty(PoToken) || string.IsNullOrEmpty(VisitorData);

        /// <summary>
        /// Gets the remaining time until expiry
        /// </summary>
        public TimeSpan TimeUntilExpiry => ExpiryUtc - DateTime.UtcNow;
    }

    /// <summary>
    /// Represents client session configuration and state
    /// </summary>
    public record ClientSessionInfo(
        SessionTokens? Tokens,
        Cookie[]? Cookies,
        string GeographicalLocation = "US")
    {
        /// <summary>
        /// Checks if the session has valid authentication data
        /// </summary>
        public bool HasValidTokens => Tokens?.IsValid == true;

        /// <summary>
        /// Checks if the session has cookies
        /// </summary>
        public bool HasCookies => Cookies?.Length > 0;

        /// <summary>
        /// Gets a summary of the authentication methods available
        /// </summary>
        public string AuthenticationSummary =>
            $"Tokens: {(HasValidTokens ? "Valid" : "Invalid/Missing")}, " +
            $"Cookies: {(HasCookies ? $"{Cookies!.Length} cookies" : "None")}";

        /// <summary>
        /// Compares this session info with another to detect if authentication has changed
        /// </summary>
        public bool IsEquivalentTo(ClientSessionInfo? other)
        {
            if (other == null) return false;

            return Tokens?.PoToken == other.Tokens?.PoToken &&
                   Tokens?.VisitorData == other.Tokens?.VisitorData &&
                   GeographicalLocation == other.GeographicalLocation &&
                   CookiesAreEquivalent(Cookies, other.Cookies);
        }

        private static bool CookiesAreEquivalent(Cookie[]? cookies1, Cookie[]? cookies2)
        {
            if (cookies1 == null && cookies2 == null) return true;
            if (cookies1 == null || cookies2 == null) return false;
            if (cookies1.Length != cookies2.Length) return false;

            return cookies1.Zip(cookies2).All(pair =>
                pair.First.Name == pair.Second.Name &&
                pair.First.Value == pair.Second.Value);
        }
    }
}