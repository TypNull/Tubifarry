namespace Tubifarry.Notifications.FlareSolverr
{
    public interface IFlareSolverrService
    {
        bool IsEnabled { get; }
        int MaxRetries { get; }

        ProtectionSolution? GetOrSolveChallenge(string host, string url, bool forceNew = false);

        void InvalidateSolution(string host);

        bool HasValidSolution(string host);
    }

    public record ProtectionSolution(
        FlareCookie[] Cookies,
        string? UserAgent,
        DateTime ExpiryUtc,
        string Host)
    {
        public bool IsValid => DateTime.UtcNow < ExpiryUtc && Cookies.Length > 0;
        public TimeSpan TimeUntilExpiry => ExpiryUtc - DateTime.UtcNow;
    }
}