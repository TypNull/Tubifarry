using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tubifarry.Core.Model
{
    public class FileCache
    {
        private readonly string _cacheDirectory;
        private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

        public FileCache(string cacheDirectory)
        {
            if (!Directory.Exists(cacheDirectory))
                Directory.CreateDirectory(cacheDirectory);
            _cacheDirectory = cacheDirectory;

            CleanupOldCacheFiles();
        }

        /// <summary>
        /// Deletes any JSON files stored directly in the root cache directory,
        /// as these are from the old file naming scheme.
        /// </summary>
        private void CleanupOldCacheFiles()
        {
            try
            {
                foreach (string file in Directory.GetFiles(_cacheDirectory, "*.json", SearchOption.TopDirectoryOnly))
                    File.Delete(file);
            }
            catch
            {

            }
        }

        /// <summary>
        /// Retrieves cached data for the given key if available and not expired.
        /// </summary>
        public async Task<T?> GetAsync<T>(string cacheKey)
        {
            string cacheFilePath = GetCacheFilePath(cacheKey);

            if (!File.Exists(cacheFilePath))
                return default;

            string json = await File.ReadAllTextAsync(cacheFilePath);
            CachedData<T>? cachedData = JsonSerializer.Deserialize<CachedData<T>>(json);

            if (cachedData == null || DateTime.UtcNow - cachedData.CreatedAt > cachedData.ExpirationDuration)
            {
                try
                {
                    File.Delete(cacheFilePath);
                }
                catch { }
                return default;
            }

            return cachedData.Data;
        }

        /// <summary>
        /// Caches the provided data with the specified expiration duration.
        /// </summary>
        public async Task SetAsync<T>(string cacheKey, T data, TimeSpan expirationDuration)
        {
            string cacheFilePath = GetCacheFilePath(cacheKey);

            string directory = Path.GetDirectoryName(cacheFilePath)!;
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            CachedData<T> cachedData = new()
            {
                Data = data,
                CreatedAt = DateTime.UtcNow,
                ExpirationDuration = expirationDuration
            };

            string json = JsonSerializer.Serialize(cachedData, _jsonOptions);
            await File.WriteAllTextAsync(cacheFilePath, json);
        }

        /// <summary>
        /// Checks whether a valid cache file exists for the given key.
        /// </summary>
        public bool IsCacheValid(string cacheKey, TimeSpan expirationDuration)
        {
            string cacheFilePath = GetCacheFilePath(cacheKey);

            if (!File.Exists(cacheFilePath))
                return false;

            string json = File.ReadAllText(cacheFilePath);
            CachedData<object>? cachedData = JsonSerializer.Deserialize<CachedData<object>>(json);

            return cachedData != null && DateTime.UtcNow - cachedData.CreatedAt <= expirationDuration;
        }

        /// <summary>
        /// Computes the file path for a given cache key using an MD5 hash and sharding.
        /// </summary>
        private string GetCacheFilePath(string cacheKey)
        {
            using MD5 md5 = MD5.Create();
            byte[] hashBytes = md5.ComputeHash(Encoding.UTF8.GetBytes(cacheKey));
            string hashString = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();

            string subdirectory = hashString.Substring(0, 2);
            string fileName = $"{hashString}.json";

            return Path.Combine(_cacheDirectory, subdirectory, fileName);
        }

        /// <summary>
        /// Ensures that the cache directory is writable and path length is valid.
        /// </summary>
        public void CheckDirectory()
        {
            try
            {

                if (!Directory.Exists(_cacheDirectory))
                    Directory.CreateDirectory(_cacheDirectory);

                string testFile = Path.Combine(_cacheDirectory, "test.tmp");
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);

                int maxPath = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? 255 : 4040;
                int maxCachePathLength = _cacheDirectory.Length + 40;
                if (maxCachePathLength >= maxPath)
                    throw new PathTooLongException($"Cache path exceeds OS limits ({maxCachePathLength} characters). Use a shorter base directory.");

            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Cache directory validation failed: {ex.Message}", ex);
            }
        }

        private class CachedData<T>
        {
            public T? Data { get; set; }
            public DateTime CreatedAt { get; set; }
            public TimeSpan ExpirationDuration { get; set; }
        }
    }
}
