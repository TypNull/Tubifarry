using FuzzySharp;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Exceptions;
using NzbDrone.Core.Parser.Model;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Web;
using System.Xml.Linq;
using Tubifarry.Core.Records;
using Tubifarry.Core.Utilities;

namespace Tubifarry.ImportLists.ArrStack
{
    /// <summary>
    /// Handles MusicBrainz API integration for finding soundtrack albums.
    /// </summary>
    internal partial class ArrSoundtrackImportParser : IParseImportListResponse
    {
        private static readonly string[] SoundtrackTerms = ["soundtrack", "ost", "score", "original soundtrack", "film score"];
        private const string MusicBrainzBaseUrl = "https://musicbrainz.org/ws/2";

        // MusicBrainz requires 1 request per second
        private static readonly SemaphoreSlim RateLimiter = new(1, 1);
        private const int RateLimitDelayMs = 1100;
        private const int SearchResultLimit = 10;
        private const double SimilarityThreshold = 0.80;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly Logger _logger;
        private readonly IHttpClient _httpClient;
        private readonly CacheService _cacheService;
        public ArrSoundtrackImportSettings Settings { get; set; }

        public ArrSoundtrackImportParser(ArrSoundtrackImportSettings settings, IHttpClient httpClient)
        {
            Settings = settings;
            _httpClient = httpClient;
            _logger = NzbDroneLogger.GetLogger(this);

            _cacheService = new CacheService
            {
                CacheType = (CacheType)settings.RequestCacheType,
                CacheDirectory = settings.CacheDirectory,
                CacheDuration = TimeSpan.FromDays(settings.CacheRetentionDays)
            };
        }

        public IList<ImportListItemInfo> ParseResponse(ImportListResponse importListResponse) => ParseResponseAsync(importListResponse).GetAwaiter().GetResult();

        public async Task<List<ImportListItemInfo>> ParseResponseAsync(ImportListResponse importListResponse)
        {
            List<ImportListItemInfo> results = [];

            if (string.IsNullOrWhiteSpace(importListResponse.Content))
            {
                _logger.Warn("Empty response content received from Arr application");
                return results;
            }

            try
            {
                await foreach (ArrMedia? media in DeserializeMediaItemsAsync(importListResponse.Content))
                {
                    if (media == null || string.IsNullOrWhiteSpace(media.Title))
                        continue;

                    List<ImportListItemInfo> mediaResults = await ProcessMediaItem(media);
                    results.AddRange(mediaResults);
                }
            }
            catch (JsonException ex)
            {
                _logger.Error(ex, "Failed to parse JSON response from Arr application");
                throw new ImportListException(importListResponse, "Invalid JSON response from Arr application", ex);
            }

            _logger.Debug($"Soundtrack discovery completed. Found {results.Count} albums from {results.GroupBy(r => r.Artist).Count()} media items");
            return results;
        }

        private static async IAsyncEnumerable<ArrMedia?> DeserializeMediaItemsAsync(string content)
        {
            await using MemoryStream stream = new(Encoding.UTF8.GetBytes(content));
            await foreach (ArrMedia? item in JsonSerializer.DeserializeAsyncEnumerable<ArrMedia>(stream, JsonOptions))
            {
                yield return item;
            }
        }

        private async Task<List<ImportListItemInfo>> ProcessMediaItem(ArrMedia media)
        {
            string cacheKey = GenerateMediaCacheKey(media);
            MediaProcessingResult cachedResults = await _cacheService.FetchAndCacheAsync(cacheKey, async () => await FetchSoundtracksForMedia(media));

            if (cachedResults?.ImportListItems?.Any() == true)
            {
                _logger.Trace($"Found {cachedResults.ImportListItems.Count} soundtracks for '{media.Title}'");
                return cachedResults.ImportListItems;
            }
            return [];
        }

        private async Task<MediaProcessingResult> FetchSoundtracksForMedia(ArrMedia media)
        {
            MediaProcessingResult result = new() { Media = media };

            try
            {
                List<MusicBrainzSearchItem> searchResults = await SearchMusicBrainzSoundtracks(media.Title);
                if (searchResults.Count == 0)
                {
                    _logger.Debug("No soundtrack matches found for '{0}'", media.Title);
                    return result;
                }

                result.SearchResults = searchResults;
                List<MusicBrainzAlbumItem> validAlbums = [];
                List<ImportListItemInfo> importItems = [];

                foreach (MusicBrainzSearchItem searchItem in searchResults)
                {
                    if (string.IsNullOrWhiteSpace(searchItem.AlbumId))
                        continue;

                    MusicBrainzAlbumItem? albumDetails = await FetchAlbumDetails(searchItem.AlbumId);
                    if (albumDetails == null)
                        continue;

                    validAlbums.Add(albumDetails);
                    if (IsGoodMatch(albumDetails, media.Title))
                    {
                        ImportListItemInfo importItem = CreateImportItem(searchItem, albumDetails);
                        importItems.Add(importItem);
                        _logger.Trace($"Added soundtrack: '{albumDetails.Title}' by '{albumDetails.Artist}' for '{media.Title}'");
                    }
                }

                result.AlbumDetails = validAlbums;
                result.ImportListItems = importItems;
            }
            catch (HttpException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
            {
                _logger.Warn($"MusicBrainz rate limit exceeded for '{media.Title}'");
                await Task.Delay(10 * RateLimitDelayMs);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to fetch soundtracks for media item '{0}'", media.Title);
            }

            return result;
        }

        private async Task<List<MusicBrainzSearchItem>> SearchMusicBrainzSoundtracks(string title)
        {
            await RateLimiter.WaitAsync();
            try
            {
                await Task.Delay(RateLimitDelayMs);

                string searchUrl = BuildSearchUrl(title);
                _logger.Trace("Searching MusicBrainz: {0}", searchUrl);

                HttpRequest request = new(searchUrl);
                HttpResponse response = await _httpClient.GetAsync(request);

                return ParseSearchResponse(response.Content);
            }
            finally
            {
                RateLimiter.Release();
            }
        }

        private string BuildSearchUrl(string title)
        {
            string baseUrl = $"{MusicBrainzBaseUrl}/release";

            if (Settings.UseStrongMusicBrainzSearch)
            {
                string normalizedTitle = NormalizeTitle(title);
                
                var words = normalizedTitle.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => w.Length > 2)
                    .Select(w => EscapeLuceneQuery(w));
                
                string titleQuery = string.Join(" AND ", words.Select(w => $"release:{w}"));
                string query = $"({titleQuery}) AND primarytype:(Album OR Single) AND secondarytype:Soundtrack";
                return $"{baseUrl}?query={HttpUtility.UrlEncode(query)}&limit={SearchResultLimit}";
            }
            else
            {
                string query = $"{title} soundtrack";
                return $"{baseUrl}?query={HttpUtility.UrlEncode(query)}&limit={SearchResultLimit}";
            }
        }

        private List<MusicBrainzSearchItem> ParseSearchResponse(string xmlContent)
        {
            try
            {
                XDocument doc = XDocument.Parse(xmlContent);
                XNamespace ns = "http://musicbrainz.org/ns/mmd-2.0#";
                List<XElement> releases = doc.Descendants(ns + "release").ToList();
                return releases.Select(release => MusicBrainzSearchItem.FromXml(release, ns))
                              .Where(item => item != null)
                              .ToList();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to parse MusicBrainz search response");
                return [];
            }
        }

        private async Task<MusicBrainzAlbumItem?> FetchAlbumDetails(string albumId) => await _cacheService.FetchAndCacheAsync(GenerateAlbumDetailsCacheKey(albumId), async () =>
            {
                await RateLimiter.WaitAsync();
                try
                {
                    await Task.Delay(RateLimitDelayMs);

                    string detailsUrl = $"{MusicBrainzBaseUrl}/release-group/{albumId}";
                    HttpRequest request = new(detailsUrl);
                    HttpResponse response = await _httpClient.GetAsync(request);

                    return ParseAlbumDetails(response.Content, albumId);
                }
                finally
                {
                    RateLimiter.Release();
                }
            });

        private MusicBrainzAlbumItem? ParseAlbumDetails(string xmlContent, string albumId)
        {
            try
            {
                XDocument doc = XDocument.Parse(xmlContent);
                XNamespace ns = "http://musicbrainz.org/ns/mmd-2.0#";
                XElement? releaseGroup = doc.Descendants(ns + "release-group").FirstOrDefault();
                if (releaseGroup == null)
                    return null;

                return MusicBrainzAlbumItem.FromXml(releaseGroup, ns);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to parse album details for ID: {0}", albumId);
                return null;
            }
        }

        private bool IsGoodMatch(MusicBrainzAlbumItem album, string originalTitle)
        {
            if (string.IsNullOrWhiteSpace(album.Title))
                return false;

            bool hasSoundtrackType = album.SecondaryTypes?.Any(t => string.Equals(t, "Soundtrack", StringComparison.OrdinalIgnoreCase)) ?? false;
            if (!hasSoundtrackType)
                return false;

            string primaryType = album.PrimaryType ?? string.Empty;
            bool isValidPrimaryType = string.Equals(primaryType, "Album", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(primaryType, "Single", StringComparison.OrdinalIgnoreCase);
            if (!isValidPrimaryType)
                return false;

            bool hasCompilationType = album.SecondaryTypes?.Any(t => string.Equals(t, "Compilation", StringComparison.OrdinalIgnoreCase)) ?? false;
            if (hasCompilationType)
                return false;

            int similarity = Fuzz.Ratio(album.Title, originalTitle);
            bool containsMovieAndSoundtrack = ContainsMovieNameAndSoundtrack(album.Title, originalTitle);
            return similarity > SimilarityThreshold * 100 || containsMovieAndSoundtrack;
        }

        private static bool ContainsMovieNameAndSoundtrack(string releaseTitle, string movieTitle)
        {
            string lowerReleaseTitle = releaseTitle.ToLowerInvariant();
            string lowerMovieTitle = movieTitle.ToLowerInvariant();
            bool containsMovieName = lowerReleaseTitle.Contains(lowerMovieTitle);
            bool containsSoundtrackTerm = SoundtrackTerms.Any(term => lowerReleaseTitle.Contains(term, StringComparison.OrdinalIgnoreCase));
            return containsMovieName && containsSoundtrackTerm;
        }

        private static string NormalizeTitle(string title)
        {
            foreach (string term in SoundtrackTerms)
                title = title.Replace(term, "", StringComparison.OrdinalIgnoreCase);
            
            var numberReplacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "one", "1" }, { "two", "2" }, { "three", "3" }, { "four", "4" },
                { "five", "5" }, { "six", "6" }, { "seven", "7" }, { "eight", "8" },
                { "nine", "9" }, { "ten", "10" }
            };
            
            foreach (var replacement in numberReplacements)
                title = Regex.Replace(title, $@"\b{replacement.Key}\b", replacement.Value, RegexOptions.IgnoreCase);
            
            title = NormalizeTitleEmptyRegex().Replace(title, "").Trim();
            return NormalizeTitleSpaceRegex().Replace(title, " ");
        }

        private static string EscapeLuceneQuery(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return query;

            foreach (string? ch in new[] { "+", "-", "&&", "||", "!", "(", ")", "{", "}", "[", "]", "^", "\"", "~", "*", "?", ":", "\\", "/" })
            {
                query = query.Replace(ch, $"\\{ch}");
            }

            return query;
        }

        private static ImportListItemInfo CreateImportItem(MusicBrainzSearchItem searchItem, MusicBrainzAlbumItem albumDetails) => new()
        {
            Artist = albumDetails.Artist ?? searchItem.Artist ?? "Unknown Artist",
            ArtistMusicBrainzId = albumDetails.ArtistId ?? searchItem.ArtistId,
            Album = albumDetails.Title ?? searchItem.Title ?? "Unknown Album",
            AlbumMusicBrainzId = albumDetails.AlbumId ?? searchItem.AlbumId,
            ReleaseDate = albumDetails.ReleaseDate != DateTime.MinValue ? albumDetails.ReleaseDate : searchItem.ReleaseDate
        };

        private static string GenerateMediaCacheKey(ArrMedia media) => $"media_{media.Id}_{GenerateStringHash(media.Title)}";

        private static string GenerateAlbumDetailsCacheKey(string albumId) => $"album_details_{albumId}";

        private static string GenerateStringHash(string input)
        {
            if (string.IsNullOrEmpty(input))
                return "empty";

            HashCode hash = new();
            hash.Add(input);
            return hash.ToHashCode().ToString("x8");
        }

        /// <summary>
        /// Represents the complete processing result for a media item
        /// </summary>
        private class MediaProcessingResult
        {
            public ArrMedia? Media { get; set; }
            public List<ImportListItemInfo> ImportListItems { get; set; } = [];
            public List<MusicBrainzSearchItem> SearchResults { get; set; } = [];
            public List<MusicBrainzAlbumItem> AlbumDetails { get; set; } = [];
        }

        [GeneratedRegex(@"[^a-zA-Z0-9\s]")]
        private static partial Regex NormalizeTitleEmptyRegex();
        [GeneratedRegex(@"\s+")]
        private static partial Regex NormalizeTitleSpaceRegex();
    }
}
