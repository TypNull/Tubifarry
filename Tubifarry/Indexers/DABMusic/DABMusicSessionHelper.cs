using NLog;
using NzbDrone.Common.Http;
using System.Net;
using System.Text.Json;

namespace Tubifarry.Indexers.DABMusic
{
    public record DABMusicSession(string SessionCookie, DateTime ExpiryUtc, string Email, string Password, string BaseUrl)
    {
        public bool IsValid => !string.IsNullOrEmpty(SessionCookie) && DateTime.UtcNow < ExpiryUtc;
        public TimeSpan TimeUntilExpiry => ExpiryUtc - DateTime.UtcNow;
    }

    public record DABMusicLoginRequest(string Email, string Password);
    public record DABMusicUser(int Id, string Username, string Email);

    public interface IDABMusicSessionManager
    {
        DABMusicSession? GetOrCreateSession(string baseUrl, string email, string password, bool forceNew = false);
        void InvalidateSession(string email);
        bool HasValidSession(string email);
    }

    public class DABMusicSessionHelper(IHttpClient httpClient, Logger logger) : IDABMusicSessionManager
    {
        private readonly IHttpClient _httpClient = httpClient;
        private readonly Logger _logger = logger;
        private readonly Dictionary<string, DABMusicSession> _sessions = [];
        private static readonly TimeSpan SessionExpiry = TimeSpan.FromDays(5);
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        public DABMusicSession? GetOrCreateSession(string baseUrl, string email, string password, bool forceNew = false)
        {
            if (!forceNew && _sessions.TryGetValue(email, out DABMusicSession? existing))
            {
                if (existing.IsValid)
                {
                    _logger.Trace($"Using existing session for {email}, expires in {existing.TimeUntilExpiry}");
                    return existing;
                }

                _logger.Debug($"Session expired for {email}, renewing with stored credentials");
                return Login(existing.BaseUrl, existing.Email, existing.Password);
            }

            _logger.Trace($"Creating new session for {email}");
            return Login(baseUrl, email, password);
        }

        public void InvalidateSession(string email)
        {
            if (_sessions.Remove(email))
                _logger.Info("Invalidated session for {email}");

        }

        public bool HasValidSession(string email) => _sessions.TryGetValue(email, out DABMusicSession? session) && session.IsValid;

        private DABMusicSession? Login(string baseUrl, string email, string password)
        {
            try
            {
                HttpRequest request = new HttpRequestBuilder($"{baseUrl.TrimEnd('/')}/api/auth/login")
                    .SetHeader("Content-Type", "application/json")
                    .SetHeader("User-Agent", Tubifarry.UserAgent)
                    .Post()
                    .Build();

                request.SetContent(JsonSerializer.Serialize(new DABMusicLoginRequest(email, password), _jsonOptions));

                HttpResponse response = _httpClient.Execute(request);

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    _logger.Error($"Login failed for {email} with status {response.StatusCode}: {response.Content}");
                    return null;
                }

                string? sessionCookie = ExtractSessionCookie(response);
                if (string.IsNullOrEmpty(sessionCookie))
                {
                    _logger.Error($"No session cookie received from login response for {email}");
                    return null;
                }

                DateTime expiry = DateTime.UtcNow.Add(SessionExpiry);

                DABMusicSession session = new(sessionCookie, expiry, email, password, baseUrl);
                _sessions[email] = session;

                _logger.Info($"Successfully logged in as {email}, session expires {expiry:yyyy-MM-dd HH:mm:ss} UTC");
                return session;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error during login for {Email}", email);
                return null;
            }
        }

        private string? ExtractSessionCookie(HttpResponse response)
        {
            if (!response.Headers.ContainsKey("Set-Cookie"))
            {
                _logger.Warn("No 'Set-Cookie' header found in login response");
                return null;
            }

            string[]? setCookieValues = response.Headers.GetValues("Set-Cookie");
            if (setCookieValues == null)
                return null;

            foreach (string cookieHeader in setCookieValues)
            {
                int sessionIndex = cookieHeader.IndexOf("session=", StringComparison.OrdinalIgnoreCase);
                if (sessionIndex >= 0)
                {
                    string sessionPart = cookieHeader[sessionIndex..];
                    int firstSemicolon = sessionPart.IndexOf(';');

                    return firstSemicolon > 0 ? sessionPart[..firstSemicolon] : sessionPart;
                }
            }

            _logger.Warn("No session cookie found in Set-Cookie headers");
            return null;
        }
    }
}
