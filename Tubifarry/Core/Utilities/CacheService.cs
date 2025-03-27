﻿using NLog;
using NzbDrone.Common.Instrumentation;
using System.Collections.Concurrent;
using Tubifarry.Core.Model;
using Tubifarry.ImportLists.WantedList;

namespace Tubifarry.Core.Utilities
{
    public class CacheService
    {
        private readonly ConcurrentDictionary<string, CachedData<object>> _memoryCache = new();
        private Lazy<FileCache>? _permanentCache;
        private readonly Logger _logger;

        public CacheType CacheType { get; set; } = CacheType.Memory;
        public TimeSpan CacheDuration { get; set; } = TimeSpan.FromDays(7);

        private string? _cacheDirectory;
        public string? CacheDirectory
        {
            get => _cacheDirectory;
            set
            {
                if (_cacheDirectory != value)
                {
                    _cacheDirectory = value;
                    _permanentCache = null;
                }
            }
        }

        public CacheService() => _logger = NzbDroneLogger.GetLogger(this);

        private FileCache PermanentCache
        {
            get
            {
                if (_permanentCache == null)
                {
                    if (string.IsNullOrEmpty(CacheDirectory))
                        throw new InvalidOperationException("CacheDirectory must be set for permanent cache.");
                    _permanentCache = new Lazy<FileCache>(() => new FileCache(CacheDirectory!));
                }
                return _permanentCache.Value;
            }
        }

        public async Task<TData?> GetAsync<TData>(string key)
        {
            if (CacheType == CacheType.Permanent)
                return await PermanentCache.GetAsync<TData>(key);

            if (_memoryCache.TryGetValue(key, out CachedData<object>? entry) && !IsExpired(entry))
                return entry.Data is TData data ? data : default;
            return default;
        }

        public async Task SetAsync<TData>(string key, TData value)
        {
            if (CacheType == CacheType.Permanent)
            {
                await PermanentCache.SetAsync(key, value, CacheDuration);
            }
            else
            {
                _memoryCache[key] = new CachedData<object>
                {
                    Data = value!,
                    CreatedAt = DateTime.Now,
                    ExpirationDuration = CacheDuration,
                };
            }
        }

        public async Task<TData> FetchAndCacheAsync<TData>(string key, Func<Task<TData>> fetch)
        {
            TData? cached = await GetAsync<TData>(key);
            if (cached != null)
            {
                _logger.Trace($"Cache hit: {key}");
                return cached;
            }

            _logger.Debug($"Cache miss: {key}");
            TData freshData = await fetch();
            await SetAsync(key, freshData);
            return freshData;
        }

        public async Task<TData> UpdateAsync<TData>(string key, Func<TData?, Task<TData>> updateFunc)
        {
            TData? current = await GetAsync<TData>(key);
            TData updated = await updateFunc(current);
            await SetAsync(key, updated);
            return updated;
        }

        private bool IsExpired(CachedData<object> entry) =>
            (DateTime.Now - entry.CreatedAt) > CacheDuration;
    }
}
