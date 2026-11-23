#if !MASTER_BRANCH
using NzbDrone.Common.Instrumentation;
using System.Collections.Concurrent;

namespace Tubifarry.Core.Telemetry
{
    public class SearchContextBuffer : ISearchContextBuffer
    {
        private readonly ConcurrentDictionary<string, SlskdBufferedContext> _contextBySearchId = new();
        private readonly ConcurrentDictionary<string, SlskdBufferedContext> _contextByDownloadId = new();
        private readonly ConcurrentDictionary<string, DateTime> _recentImports = new();
        private static readonly TimeSpan ContextExpiry = TimeSpan.FromHours(1);
        private static readonly TimeSpan ImportTrackingExpiry = TimeSpan.FromDays(8);
        private DateTime _lastCleanup = DateTime.UtcNow;

        public void LogSearch(string searchId, string query, string? artist, string? album, string strategy, int resultCount)
        {
            CleanupIfNeeded();

            SlskdBufferedContext context = new()
            {
                SearchId = searchId,
                SearchQuery = query,
                Artist = artist,
                Album = album,
                Strategy = strategy,
                TotalResults = resultCount
            };
            context.Breadcrumbs.Add($"Search: '{query}' via {strategy} → {resultCount} results");

            _contextBySearchId[searchId] = context;
        }

        public void LogSearchSettings(string searchId, int trackCountFilter, bool normalizedSearch, bool appendYear, bool handleVolumeVariations, bool useFallbackSearch, bool useTrackFallback, int minimumResults, bool hasTemplates)
        {
            if (_contextBySearchId.TryGetValue(searchId, out SlskdBufferedContext? context))
            {
                context.SettingsTrackCountFilter = trackCountFilter;
                context.SettingsNormalizedSearch = normalizedSearch;
                context.SettingsAppendYear = appendYear;
                context.SettingsHandleVolumeVariations = handleVolumeVariations;
                context.SettingsUseFallbackSearch = useFallbackSearch;
                context.SettingsUseTrackFallback = useTrackFallback;
                context.SettingsMinimumResults = minimumResults;
                context.SettingsHasTemplates = hasTemplates;
            }
        }

        public void LogExpectedTracks(string searchId, List<string> trackNames, int expectedCount)
        {
            if (_contextBySearchId.TryGetValue(searchId, out SlskdBufferedContext? context))
            {
                context.ExpectedTracks = trackNames;
                context.ExpectedTrackCount = expectedCount;
            }
        }

        public void LogParseResult(
            string searchId,
            string folderPath,
            string regexMatchType,
            int fuzzyArtistScore,
            int fuzzyAlbumScore,
            int fuzzyArtistTokenSort,
            int fuzzyAlbumTokenSort,
            int priority,
            string codec,
            int bitrate,
            int bitDepth,
            int trackCountExpected,
            int trackCountActual,
            string username,
            bool hasFreeSlot,
            int queueLength,
            List<string>? directoryFiles,
            bool isInteractive)
        {
            if (!_contextBySearchId.TryGetValue(searchId, out SlskdBufferedContext? context))
            {
                context = new SlskdBufferedContext { SearchId = searchId, CreatedAt = DateTime.UtcNow };
                _contextBySearchId[searchId] = context;
            }

            context.FolderPath = folderPath;
            context.RegexMatchType = regexMatchType;
            context.FuzzyArtistScore = fuzzyArtistScore;
            context.FuzzyAlbumScore = fuzzyAlbumScore;
            context.FuzzyArtistTokenSort = fuzzyArtistTokenSort;
            context.FuzzyAlbumTokenSort = fuzzyAlbumTokenSort;
            context.Priority = priority;
            context.Codec = codec;
            context.Bitrate = bitrate;
            context.BitDepth = bitDepth;
            context.TrackCountExpected = trackCountExpected;
            context.TrackCountActual = trackCountActual;
            context.Username = username;
            context.HasFreeSlot = hasFreeSlot;
            context.QueueLength = queueLength;
            context.DirectoryFiles = directoryFiles;
            context.IsInteractive = isInteractive;

            context.AllCandidates.Add(new ParseCandidate
            {
                FolderName = Path.GetFileName(folderPath.TrimEnd('\\', '/')),
                FullPath = folderPath,
                RegexMatchType = regexMatchType,
                FuzzyArtist = fuzzyArtistScore,
                FuzzyAlbum = fuzzyAlbumScore,
                Priority = priority,
                TrackCount = trackCountActual,
                Codec = codec,
                Username = username,
                WasGrabbed = false
            });
        }

        public void UpdateSearchResultCount(string searchId, int actualResultCount)
        {
            if (_contextBySearchId.TryGetValue(searchId, out SlskdBufferedContext? context))
            {
                context.TotalResults = actualResultCount;
                int idx = context.Breadcrumbs.FindIndex(b => b.StartsWith("Search:"));
                if (idx >= 0)
                    context.Breadcrumbs[idx] = $"Search: '{context.SearchQuery}' via {context.Strategy} → {actualResultCount} results";
            }
        }

        public void LogGrab(string searchId, string downloadId, bool isInteractive)
        {
            NzbDroneLogger.GetLogger(this).Debug($"[SearchContextBuffer] LogGrab: searchId={searchId} knownSearchIds=[{string.Join(",", _contextBySearchId.Keys)}]");
            if (_contextBySearchId.TryRemove(searchId, out SlskdBufferedContext? context))
            {
                context.DownloadId = downloadId;
                context.IsInteractive = isInteractive;

                // Mark the grabbed candidate
                ParseCandidate? grabbed = context.AllCandidates.FirstOrDefault(c => c.FullPath == context.FolderPath);
                if (grabbed != null)
                    grabbed.WasGrabbed = true;

                // Calculate selection analysis
                if (context.AllCandidates.Count > 0)
                {
                    context.OurTopPriority = context.AllCandidates.Max(c => c.Priority);
                    context.GrabbedPriority = grabbed?.Priority;
                    context.LidarrUsedOurTop = context.OurTopPriority == context.GrabbedPriority;

                    // Add summary breadcrumb
                    ParseCandidate best = context.AllCandidates.OrderByDescending(c => c.Priority).First();
                    int rank = grabbed == null ? 0 : context.AllCandidates.OrderByDescending(c => c.Priority).ToList().IndexOf(grabbed) + 1;
                    context.Breadcrumbs.Add($"Parsed {context.AllCandidates.Count} candidates (best: priority={best.Priority}, regex={best.RegexMatchType})");
                    context.Breadcrumbs.Add($"Grabbed: '{grabbed?.FolderName}' (priority={grabbed?.Priority}, rank=#{rank}, {(isInteractive ? "interactive" : "auto")})");
                }
                else
                {
                    context.Breadcrumbs.Add($"Grab: {(isInteractive ? "interactive" : "auto-selected")}");
                }

                _contextByDownloadId[downloadId] = context;
            }
        }

        public SlskdBufferedContext? GetContext(string downloadId)
        {
            _contextByDownloadId.TryGetValue(downloadId, out SlskdBufferedContext? context);
            return context;
        }

        public SlskdBufferedContext? GetAndRemoveContext(string downloadId)
        {
            if (_contextByDownloadId.TryRemove(downloadId, out SlskdBufferedContext? context))
                return context;
            return null;
        }

        public void AddBreadcrumb(string downloadId, string message)
        {
            if (_contextByDownloadId.TryGetValue(downloadId, out SlskdBufferedContext? context))
                context.Breadcrumbs.Add(message);
        }

        public void RecordImport(string albumKey)
        {
            CleanupIfNeeded();
            _recentImports[albumKey] = DateTime.UtcNow;
        }

        public bool WasRecentlyImported(string albumKey, out int daysSinceImport)
        {
            daysSinceImport = 0;
            if (_recentImports.TryGetValue(albumKey, out DateTime importTime))
            {
                TimeSpan elapsed = DateTime.UtcNow - importTime;
                if (elapsed.TotalDays <= 7)
                {
                    daysSinceImport = (int)Math.Ceiling(elapsed.TotalDays);
                    return true;
                }
            }
            return false;
        }

        private void CleanupIfNeeded()
        {
            if (DateTime.UtcNow - _lastCleanup < TimeSpan.FromMinutes(10))
                return;

            _lastCleanup = DateTime.UtcNow;
            DateTime now = DateTime.UtcNow;

            List<string> expiredSearchIds = _contextBySearchId
                .Where(kvp => now - kvp.Value.CreatedAt > ContextExpiry)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (string key in expiredSearchIds)
                _contextBySearchId.TryRemove(key, out _);

            List<string> expiredDownloadIds = _contextByDownloadId
                .Where(kvp => now - kvp.Value.CreatedAt > ContextExpiry)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (string key in expiredDownloadIds)
                _contextByDownloadId.TryRemove(key, out _);

            List<string> expiredImports = _recentImports
                .Where(kvp => now - kvp.Value > ImportTrackingExpiry)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (string key in expiredImports)
                _recentImports.TryRemove(key, out _);
        }
    }
}
#endif
