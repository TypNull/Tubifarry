using System.Text.Json;

namespace Tubifarry.Core
{
    public class FileCache
    {
        private readonly string _cacheDirectory;

        public FileCache(string cacheDirectory)
        {
            if (!Directory.Exists(cacheDirectory))
                Directory.CreateDirectory(cacheDirectory);
            _cacheDirectory = cacheDirectory;
        }

        public async Task<T?> GetAsync<T>(string cacheKey)
        {
            string cacheFilePath = Path.Combine(_cacheDirectory, $"{cacheKey}.json");

            if (!File.Exists(cacheFilePath))
                return default;

            string json = await File.ReadAllTextAsync(cacheFilePath);
            CachedData<T>? cachedData = JsonSerializer.Deserialize<CachedData<T>>(json);

            if (cachedData == null || DateTime.UtcNow - cachedData.CreatedAt > cachedData.ExpirationDuration)
            {
                // Cache is expired or invalid, delete the file
                File.Delete(cacheFilePath);
                return default;
            }

            return cachedData.Data;
        }

        public async Task SetAsync<T>(string cacheKey, T data, TimeSpan expirationDuration)
        {
            string cacheFilePath = Path.Combine(_cacheDirectory, $"{cacheKey}.json");

            CachedData<T> cachedData = new()
            {
                Data = data,
                CreatedAt = DateTime.UtcNow,
                ExpirationDuration = expirationDuration
            };

            string json = JsonSerializer.Serialize(cachedData);
            await File.WriteAllTextAsync(cacheFilePath, json);
        }

        public bool IsCacheValid(string cacheKey, TimeSpan expirationDuration)
        {
            string cacheFilePath = Path.Combine(_cacheDirectory, $"{cacheKey}.json");

            if (!File.Exists(cacheFilePath))
                return false;

            string json = File.ReadAllText(cacheFilePath);
            CachedData<object>? cachedData = JsonSerializer.Deserialize<CachedData<object>>(json);

            return cachedData != null && DateTime.UtcNow - cachedData.CreatedAt <= expirationDuration;
        }

        private class CachedData<T>
        {
            public T? Data { get; set; }
            public DateTime CreatedAt { get; set; }
            public TimeSpan ExpirationDuration { get; set; }
        }
    }
}