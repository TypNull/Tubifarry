using System.Text.RegularExpressions;

namespace Tubifarry.Core.Utilities
{
    /// <summary>
    /// Interface for User-Agent validation and parsing
    /// </summary>
    public interface IUserAgentValidator
    {
        /// <summary>
        /// Validates if a User-Agent string is allowed
        /// </summary>
        /// <param name="userAgent">User-Agent string to validate</param>
        /// <returns>True if allowed, otherwise false</returns>
        bool IsAllowed(string userAgent);

        /// <summary>
        /// Parses User-Agent into product components<
        /// /summary>
        /// <param name="userAgent">User-Agent string to parse</param>
        /// <returns>Collection of product tokens</returns>
        IEnumerable<UserAgentProduct> Parse(string userAgent);

        /// <summary>
        /// Adds pattern to allowlist
        /// </summary>
        /// <param name="pattern">Pattern to allow</param>
        void AddAllowedPattern(string pattern);

        /// <summary>
        /// Adds pattern to blacklist
        /// </summary>
        /// <param name="pattern">Pattern to block</param>
        void AddBlacklistPattern(string pattern);
    }

    /// <summary>
    /// Represents product token in User-Agent string
    /// </summary>
    public record UserAgentProduct(string Name, string Version)
    {
        public override string ToString() => Version != null ? $"{Name}/{Version}" : Name;
    }

    /// <summary>
    /// Validates User-Agents against allow/block lists
    /// </summary>
    public partial class UserAgentValidator : IUserAgentValidator
    {
        private readonly HashSet<string> _allowedExact = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Regex> _allowedRegex = [];
        private readonly HashSet<string> _blackExact = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Regex> _blackRegex = [];

        public static UserAgentValidator Instance { get; private set; } = new();

        /// <summary>
        /// Creates validator with optional initial patterns
        /// </summary>
        /// <param name="allowed">Initial allow patterns</param>
        /// <param name="blacklisted">Initial block patterns</param>
        public UserAgentValidator(IEnumerable<string>? allowed = null, IEnumerable<string>? blacklisted = null)
        {
            allowed?.ToList().ForEach(AddAllowedPattern);
            blacklisted?.ToList().ForEach(AddBlacklistPattern);
            Instance = this;
        }

        /// <inheritdoc/>
        public bool IsAllowed(string userAgent)
        {
            if (string.IsNullOrWhiteSpace(userAgent)) return false;
            if (!IsValidFormat(userAgent)) return false;
            if (_blackExact.Contains(userAgent)) return false;
            if (_blackRegex.Any(p => p.IsMatch(userAgent))) return false;
            if (_allowedExact.Count == 0 && _allowedRegex.Count == 0) return true;
            return _allowedExact.Contains(userAgent) || _allowedRegex.Any(p => p.IsMatch(userAgent));
        }

        /// <inheritdoc/>
        public IEnumerable<UserAgentProduct> Parse(string userAgent) => userAgent.Split(' ')
            .Select(t => t.Split(['/'], 2))
            .Where(p => p.Length > 0 && !string.IsNullOrWhiteSpace(p[0]))
            .Select(p => new UserAgentProduct(p[0], p.Length > 1 ? p[1] : string.Empty));

        /// <inheritdoc/>
        public void AddAllowedPattern(string pattern) => AddPattern(pattern, _allowedExact, _allowedRegex);

        /// <inheritdoc/>
        public void AddBlacklistPattern(string pattern) => AddPattern(pattern, _blackExact, _blackRegex);

        private bool IsValidFormat(string ua)
        {
            try { return Parse(ua).All(p => TokenPattern().IsMatch(p.Name) && (p.Version == null || TokenPattern().IsMatch(p.Version))); }
            catch { return false; }
        }

        /// <summary>
        /// Adds a pattern to either the exact matches or regex patterns collection
        /// </summary>
        private static void AddPattern(string p, HashSet<string> exact, List<Regex> regex)
        {
            if (string.IsNullOrWhiteSpace(p)) throw new ArgumentException("Pattern required", nameof(p));
            if (p.IndexOfAny(['*', '?', '+', '(', '[', '\\', '.']) >= 0)
            {
                try { regex.Add(new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled)); }
                catch { exact.Add(p); }
            }
            else { exact.Add(p); }
        }

        [GeneratedRegex(@"^[!#$%&'*+\-.0-9A-Z^_`a-z|~]+$")]
        private static partial Regex TokenPattern();
    }
}