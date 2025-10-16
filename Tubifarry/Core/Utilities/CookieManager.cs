using System.Net;

namespace Tubifarry.Core.Utilities
{
    internal static class CookieManager
    {
        internal static Cookie[] ParseCookieFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return [];

            List<Cookie> cookies = [];

            try
            {
                foreach (string line in File.ReadLines(filePath))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                        continue;

                    string[] parts = line.Split('\t');
                    if (parts.Length < 7)
                        continue;

                    string domain = parts[0].Trim();
                    string path = parts[2].Trim();
                    string secureFlag = parts[3].Trim();
                    string httpOnlyFlag = parts[1].Trim();
                    string expiresString = parts[4].Trim();
                    string name = parts[5].Trim();
                    string value = parts[6].Trim();

                    if (string.IsNullOrEmpty(domain) || string.IsNullOrEmpty(path) || string.IsNullOrEmpty(name))
                        continue;

                    if (!long.TryParse(expiresString, out long expires))
                        expires = 0;

                    bool isSecure = secureFlag.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                    bool isHttpOnly = httpOnlyFlag.Equals("TRUE", StringComparison.OrdinalIgnoreCase);

                    Cookie cookie = new(name, value, path, domain)
                    {
                        Secure = isSecure,
                        HttpOnly = isHttpOnly,
                        Expires = expires > 0 ? DateTimeOffset.FromUnixTimeSeconds(expires).DateTime : DateTime.MinValue
                    };

                    cookies.Add(cookie);
                }
            }
            catch { }
            return [.. cookies];
        }
    }
}