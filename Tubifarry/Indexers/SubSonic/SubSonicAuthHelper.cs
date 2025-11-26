using System.Text;

namespace Tubifarry.Indexers.SubSonic
{
    /// <summary>
    /// Helper class for SubSonic authentication
    /// Handles token generation, MD5 hashing, and URL building for secure authentication
    /// </summary>
    public static class SubSonicAuthHelper
    {
        public const string ClientName = PluginInfo.Name;
        public const string ApiVersion = "1.16.1";

        public static (string Salt, string Token) GenerateToken(string password)
        {
            string salt = GenerateSaltFromAssembly();
            string token = CalculateMd5Hash(password + salt);
            return (salt, token);
        }

        public static void AppendAuthParameters(StringBuilder urlBuilder, string username, string password, bool useTokenAuth)
        {
            string separator = urlBuilder.ToString().Contains('?') ? "&" : "?";

            urlBuilder.Append($"{separator}u={Uri.EscapeDataString(username)}");
            urlBuilder.Append($"&v={Uri.EscapeDataString(ApiVersion)}");
            urlBuilder.Append($"&c={Uri.EscapeDataString(ClientName)}");

            if (useTokenAuth)
            {
                (string salt, string token) = GenerateToken(password);
                urlBuilder.Append($"&t={token}");
                urlBuilder.Append($"&s={salt}");
            }
            else
            {
                urlBuilder.Append($"&p={Uri.EscapeDataString(password)}");
            }
        }

        private static string GenerateSaltFromAssembly() =>
            CalculateMd5Hash(PluginInfo.InformationalVersion + Tubifarry.UserAgent + Tubifarry.LastStarted)[..7];

        private static string CalculateMd5Hash(string input)
        {
            byte[] inputBytes = Encoding.UTF8.GetBytes(input);
            byte[] hashBytes = System.Security.Cryptography.MD5.HashData(inputBytes);
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }
    }
}
