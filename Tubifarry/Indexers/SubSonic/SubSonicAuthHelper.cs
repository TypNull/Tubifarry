using System.Text;

namespace Tubifarry.Indexers.SubSonic
{
    /// <summary>
    /// Helper class for SubSonic authentication
    /// Handles token generation and MD5 hashing for secure authentication
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

        private static string GenerateSaltFromAssembly()
        {
            string hash = CalculateMd5Hash(PluginInfo.InformationalVersion + Tubifarry.UserAgent + Tubifarry.LastStarted);
            return hash[..6];
        }

        private static string CalculateMd5Hash(string input)
        {
            byte[] inputBytes = Encoding.UTF8.GetBytes(input);
            byte[] hashBytes = System.Security.Cryptography.MD5.HashData(inputBytes);
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }
    }
}
