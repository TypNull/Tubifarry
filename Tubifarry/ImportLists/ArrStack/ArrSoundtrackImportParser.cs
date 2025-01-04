using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Parser.Model;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Tubifarry.Core;
using Tubifarry.ImportLists;

namespace NzbDrone.Core.ImportLists.ArrStack
{
    internal class ArrSoundtrackImportParser : IParseImportListResponse
    {
        private static readonly string[] SoundtrackTerms = { "soundtrack", "ost", "score", "original soundtrack", "film score" };

        public ArrSoundtrackImportSettings Settings { get; set; }
        public Logger Logger { get; set; }
        private readonly IHttpClient _httpClient;
        private readonly FileCache _fileCache;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public ArrSoundtrackImportParser(ArrSoundtrackImportSettings settings, Logger logger, IHttpClient httpClient)
        {
            Settings = settings;
            Logger = logger;
            _httpClient = httpClient;
            _fileCache = new FileCache(Settings.CacheDirectory);
        }


        public IList<ImportListItemInfo> ParseResponse(ImportListResponse importListResponse) => ParseResponseAsync(importListResponse).Result;

        public async Task<List<ImportListItemInfo>> ParseResponseAsync(ImportListResponse importListResponse)
        {
            List<ImportListItemInfo> itemInfos = new();

            using MemoryStream stream = new(Encoding.UTF8.GetBytes(importListResponse.Content));

            await foreach (ArrMedia? media in JsonSerializer.DeserializeAsyncEnumerable<ArrMedia>(stream, _jsonOptions))
            {
                if (media == null)
                    continue;

                try
                {
                    string cacheKey = GenerateCacheKey(media.Title, media.Id);

                    if (_fileCache.IsCacheValid(cacheKey, TimeSpan.FromDays(Settings.CacheRetentionDays)))
                    {
                        CachedData? cachedData = await _fileCache.GetAsync<CachedData>(cacheKey);
                        if (cachedData != null)
                        {
                            itemInfos.AddRange(cachedData.ImportListItems);
                            continue;
                        }
                    }

                    List<MusicBrainzSearchItem> albumInfos = await FetchAlbumInfo(media.Title);
                    await Task.Delay(1100);

                    if (albumInfos == null || !albumInfos.Any())
                        continue;

                    List<MusicBrainzAlbumItem?> savedAlbumDetails = new();
                    foreach (MusicBrainzSearchItem albumInfo in albumInfos)
                    {
                        if (albumInfo.AlbumId == null)
                            continue;

                        MusicBrainzAlbumItem? albumDetails = await FetchAlbumDetails(albumInfo.AlbumId);

                        await Task.Delay(1500);

                        if (albumDetails?.Title == null || albumDetails.Type?.ToLower() == "soundtrack")
                            continue;

                        savedAlbumDetails.Add(albumDetails);
                        double ss = CalculateSimilarity(albumDetails.Title, media.Title);
                        bool containsMovie = ContainsMovieNameAndSoundtrack(albumDetails.Title, media.Title);
                        if (ss > 0.70 || containsMovie)
                        {
                            ImportListItemInfo importItem = CreateImportItem(albumInfo, albumDetails);
                            itemInfos.Add(importItem);
                        }
                        else
                            Logger.Debug($"Not similar enough: {albumDetails?.Title ?? "Empty"} | {media.Title}");
                    }

                    CachedData cachedDataToSave = new()
                    {
                        ImportListItems = savedAlbumDetails
                            .Where(a => a != null)
                            .Select(a => CreateImportItem(albumInfos.First(ai => ai.AlbumId == a!.AlbumId), a!))
                            .ToList(),
                        MusicBrainzSearchItems = albumInfos,
                        MusicBrainzAlbumItems = savedAlbumDetails,
                        ArrMedia = media
                    };

                    await _fileCache.SetAsync(cacheKey, cachedDataToSave, TimeSpan.FromDays(Settings.CacheRetentionDays));
                }
                catch (Exception ex)
                {
                    if (ex.Message.Contains("503:ServiceUnavailable"))
                    {
                        Logger.Warn("Rate limit exceeded. Stopping further processing.");
                        break;
                    }
                    Logger.Error(ex, "Failed fetching search");
                }
            }
            return itemInfos;
        }

        private async Task<List<MusicBrainzSearchItem>> FetchAlbumInfo(string title)
        {
            string query = $"https://musicbrainz.org/ws/2/release?query={title} soundtrack&limit=5&offset=0";
            if (Settings.UseStrongMusicBrainzSearch)
            {
                string normalizedTitle = NormalizeTitle(title);
                string escapedTitle = EscapeLuceneQuery(normalizedTitle);

                query = Uri.EscapeDataString($"release:\"{escapedTitle}\" AND release-group-type:soundtrack");
            }

            HttpRequest request = new(query);
            HttpResponse response = await _httpClient.GetAsync(request);

            XDocument doc = XDocument.Parse(response.Content);
            XNamespace ns = "http://musicbrainz.org/ns/mmd-2.0#";
            List<XElement> releases = doc.Descendants(ns + "release").ToList();

            return releases.Select(release => MusicBrainzSearchItem.FromXml(release, ns)).ToList();
        }

        private static string NormalizeTitle(string title)
        {
            foreach (string term in SoundtrackTerms)
                title = title.Replace(term, "", StringComparison.OrdinalIgnoreCase);

            title = Regex.Replace(title, @"[^a-zA-Z0-9\s]", "").Trim().ToLower();
            return title;
        }

        private static bool ContainsMovieNameAndSoundtrack(string releaseTitle, string movieTitle)
        {
            string lowercaseReleaseTitle = releaseTitle.ToLower();
            string lowercaseMovieTitle = movieTitle.ToLower();
            bool containsMovieName = lowercaseReleaseTitle.Contains(lowercaseMovieTitle);
            bool containsSoundtrackTerm = SoundtrackTerms.Any(term => lowercaseReleaseTitle.Contains(term));

            return containsMovieName && containsSoundtrackTerm;
        }

        private static string EscapeLuceneQuery(string query)
        {
            string[] specialChars = { "+", "-", "&&", "||", "!", "(", ")", "{", "}", "[", "]", "^", "\"", "~", "*", "?", ":", "\\", "/" };
            foreach (string ch in specialChars)
                query = query.Replace(ch, $"\\{ch}");
            return query;
        }

        private async Task<MusicBrainzAlbumItem?> FetchAlbumDetails(string albumId)
        {
            HttpRequest request = new($"https://musicbrainz.org/ws/2/release-group/{albumId}");
            HttpResponse response = await _httpClient.GetAsync(request);

            XDocument doc = XDocument.Parse(response.Content);
            XNamespace ns = "http://musicbrainz.org/ns/mmd-2.0#";
            XElement? releaseGroup = doc.Descendants(ns + "release-group").FirstOrDefault();

            if (releaseGroup == null)
            {
                Logger.Warn($"No release-group found for album ID: {albumId}");
                return null;
            }
            return MusicBrainzAlbumItem.FromXml(releaseGroup, ns);
        }

        private static double CalculateSimilarity(string a, string b)
        {
            a = NormalizeTitle(a);
            b = NormalizeTitle(b);

            int levenshteinDistance = ComputeLevenshteinDistance(a, b);
            double maxLength = Math.Max(a.Length, b.Length);
            return 1.0 - (levenshteinDistance / maxLength);
        }

        private static int ComputeLevenshteinDistance(string a, string b)
        {
            int[,] dp = new int[a.Length + 1, b.Length + 1];

            for (int i = 0; i <= a.Length; i++)
                dp[i, 0] = i;

            for (int j = 0; j <= b.Length; j++)
                dp[0, j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = (a[i - 1] == b[j - 1]) ? 0 : 1;
                    dp[i, j] = Math.Min(Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1), dp[i - 1, j - 1] + cost);
                }
            }

            return dp[a.Length, b.Length];
        }

        private static ImportListItemInfo CreateImportItem(MusicBrainzSearchItem albumInfo, MusicBrainzAlbumItem albumDetails) => new()
        {
            Artist = albumDetails.Artist ?? albumInfo.Artist,
            ArtistMusicBrainzId = albumDetails.ArtistId ?? albumInfo.ArtistId,
            Album = albumDetails.Title ?? albumInfo.Title,
            AlbumMusicBrainzId = albumDetails.AlbumId ?? albumInfo.AlbumId,
            ReleaseDate = albumDetails.ReleaseDate != DateTime.MinValue ? albumDetails.ReleaseDate : albumInfo.ReleaseDate
        };

        private class CachedData
        {
            public List<ImportListItemInfo> ImportListItems { get; set; } = new();
            public List<MusicBrainzSearchItem> MusicBrainzSearchItems { get; set; } = new();
            public List<MusicBrainzAlbumItem?> MusicBrainzAlbumItems { get; set; } = new();
            public ArrMedia? ArrMedia { get; set; }
        }

        public static string GenerateCacheKey(string title, int id)
        {
            string validFilename = new(title.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray());
            string truncatedTitle = validFilename.Length > 20 ? validFilename[..20] : validFilename;

            string hashInput = $"{title}_{id}";
            using SHA256 sha256 = SHA256.Create();
            byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(hashInput));
            string hash = BitConverter.ToString(hashBytes).Replace("-", "")[..5].ToLower();
            return $"{truncatedTitle}_{hash}";
        }
    }
}