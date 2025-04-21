using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Music;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Tubifarry.Core.Replacements;

namespace Tubifarry.Indexers.Soulseek
{
    internal class SlskdRequestGenerator : IIndexerRequestGenerator<LazyIndexerPageableRequest>
    {
        private readonly SlskdIndexer _indexer;
        private readonly Logger _logger;
        private readonly IHttpClient _client;
        private readonly HashSet<string> _processedSearches = new(StringComparer.OrdinalIgnoreCase);

        private SlskdSettings Settings => _indexer.Settings;

        private static readonly Dictionary<string, int> RomanNumerals = new(StringComparer.OrdinalIgnoreCase)
        {
            { "I", 1 }, { "II", 2 }, { "III", 3 }, { "IV", 4 }, { "V", 5 },
            { "VI", 6 }, { "VII", 7 }, { "VIII", 8 }, { "IX", 9 }, { "X", 10 },
            { "XI", 11 }, { "XII", 12 }, { "XIII", 13 }, { "XIV", 14 }, { "XV", 15 },
            { "XVI", 16 }, { "XVII", 17 }, { "XVIII", 18 }, { "XIX", 19 }, { "XX", 20 }
        };

        private static readonly string[] VolumeFormats = { "Volume", "Vol.", "Vol", "v", "V" };
        private static readonly Regex PunctuationPattern = new(@"[^\w\s-&]", RegexOptions.Compiled);
        private static readonly Regex VolumePattern = new(@"(Vol(?:ume)?\.?)\s*([0-9]+|[IVXLCDM]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RomanNumeralPattern = new(@"\b([IVXLCDM]+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public SlskdRequestGenerator(SlskdIndexer indexer, IHttpClient client)
        {
            _indexer = indexer;
            _client = client;
            _logger = NzbDroneLogger.GetLogger(this);
        }

        public IndexerPageableRequestChain<LazyIndexerPageableRequest> GetRecentRequests() => new LazyIndexerPageableRequestChain(Settings.MinimumResults);

        public IndexerPageableRequestChain<LazyIndexerPageableRequest> GetSearchRequests(AlbumSearchCriteria searchCriteria)
        {
            _logger.Trace($"Setting up lazy search for album: {searchCriteria.AlbumQuery} by artist: {searchCriteria.ArtistQuery}");

            Album? album = searchCriteria.Albums.FirstOrDefault();
            List<AlbumRelease>? albumReleases = album?.AlbumReleases.Value;
            int trackCount = albumReleases?.Any() == true ? albumReleases.Min(x => x.TrackCount) : 0;
            List<string> tracks = albumReleases?.FirstOrDefault(x => x.Tracks.Value is { Count: > 0 })?.Tracks.Value?
                .Where(x => !string.IsNullOrEmpty(x.Title)).Select(x => x.Title).ToList() ?? new List<string>();

            _processedSearches.Clear();

            SearchParameters searchParams = new(
                searchCriteria.ArtistQuery,
                searchCriteria.ArtistQuery != searchCriteria.AlbumQuery ? searchCriteria.AlbumQuery : null,
                searchCriteria.AlbumYear.ToString(),
                searchCriteria.InteractiveSearch,
                trackCount,
                searchCriteria.Artist?.Metadata.Value.Aliases ?? new List<string>(),
                tracks);

            return CreateSearchChain(searchParams);
        }

        public IndexerPageableRequestChain<LazyIndexerPageableRequest> GetSearchRequests(ArtistSearchCriteria searchCriteria)
        {
            _logger.Debug($"Setting up lazy search for artist: {searchCriteria.CleanArtistQuery}");

            Album? album = searchCriteria.Albums.FirstOrDefault();
            List<AlbumRelease>? albumReleases = album?.AlbumReleases.Value;
            int trackCount = albumReleases?.Any() == true ? albumReleases.Min(x => x.TrackCount) : 0;
            List<string> tracks = albumReleases?.FirstOrDefault(x => x.Tracks.Value is { Count: > 0 })?.Tracks.Value?
                .Where(x => !string.IsNullOrEmpty(x.Title)).Select(x => x.Title).ToList() ?? new List<string>();

            _processedSearches.Clear();

            SearchParameters searchParams = new(
                searchCriteria.CleanArtistQuery,
                null,
                null,
                searchCriteria.InteractiveSearch,
                trackCount,
                searchCriteria.Artist?.Metadata.Value.Aliases ?? new List<string>(),
                tracks);

            return CreateSearchChain(searchParams);
        }

        private LazyIndexerPageableRequestChain CreateSearchChain(SearchParameters searchParams)
        {
            LazyIndexerPageableRequestChain chain = new(Settings.MinimumResults);

            // Tier 1: Base search
            _logger.Trace($"Adding Tier 1: Base search for artist='{searchParams.Artist}', album='{searchParams.Album}'");
            chain.AddTierFactory(SearchTierGenerator.CreateTier(
                () => ExecuteSearch(searchParams.Artist, searchParams.Album, searchParams.Interactive, searchParams.TrackCount)));

            if (!AnyEnhancedSearchEnabled())
            {
                _logger.Trace("No enhanced search enabled, returning chain with base tier only");
                return chain;
            }

            // Tier 2: Character normalization
            if (Settings.NormalizeSpecialCharacters)
            {
                chain.AddTierFactory(SearchTierGenerator.CreateConditionalTier(
                    () => ShouldNormalizeCharacters(searchParams.Artist, searchParams.Album),
                    () => ExecuteSearch(NormalizeSpecialCharacters(searchParams.Artist), NormalizeSpecialCharacters(searchParams.Album), searchParams.Interactive, searchParams.TrackCount)));
            }

            // Tier 3: Punctuation stripping
            if (Settings.StripPunctuation)
            {
                chain.AddTierFactory(SearchTierGenerator.CreateConditionalTier(
                    () => ShouldStripPunctuation(searchParams.Artist, searchParams.Album),
                    () => ExecuteSearch(StripPunctuation(searchParams.Artist), StripPunctuation(searchParams.Album), searchParams.Interactive, searchParams.TrackCount)));

                if (Settings.NormalizeSpecialCharacters)
                {
                    chain.AddTierFactory(SearchTierGenerator.CreateConditionalTier(
                        () => ShouldStripPunctuation(searchParams.Artist, searchParams.Album),
                        () => ExecuteSearch(NormalizeSpecialCharacters(StripPunctuation(searchParams.Artist)), NormalizeSpecialCharacters(StripPunctuation(searchParams.Album)), searchParams.Interactive, searchParams.TrackCount)));
                }
            }

            // Tier 4: Various artists handling
            if (Settings.HandleVariousArtists && searchParams.Artist != null && searchParams.Album != null)
            {
                chain.AddTierFactory(SearchTierGenerator.CreateConditionalTier(
                    () => IsVariousArtists(searchParams.Artist),
                    () => ExecuteVariousArtistsSearches(searchParams.Album, searchParams.Year, searchParams.Interactive, searchParams.TrackCount)));
            }

            // Tier 5: Volume variations
            if (Settings.HandleVolumeVariations && searchParams.Album != null)
            {
                chain.AddTierFactory(SearchTierGenerator.CreateConditionalTier(
                    () => ContainsVolumeReference(searchParams.Album),
                    () => ExecuteVariationSearches(searchParams.Artist, GenerateVolumeVariations(searchParams.Album), searchParams.Interactive, searchParams.TrackCount)));

                chain.AddTierFactory(SearchTierGenerator.CreateConditionalTier(
                    () => ShouldGenerateRomanVariations(searchParams.Album),
                    () => ExecuteVariationSearches(searchParams.Artist, GenerateRomanNumeralVariations(searchParams.Album), searchParams.Interactive, searchParams.TrackCount)));
            }

            // Tier 6+: Fallback searches
            if (Settings.UseFallbackSearch)
            {
                _logger.Trace("Adding fallback search tiers");
                AddFallbackTiers(chain, searchParams);
            }

            _logger.Trace($"Final chain: {chain.Tiers} tiers");
            return chain;
        }

        private void AddFallbackTiers(LazyIndexerPageableRequestChain chain, SearchParameters searchParams)
        {
            // Artist aliases (limit to 2)
            for (int i = 0; i < Math.Min(2, searchParams.Aliases.Count); i++)
            {
                string alias = searchParams.Aliases[i];
                if (alias.Length > 3)
                {
                    chain.AddTierFactory(SearchTierGenerator.CreateTier(
                        () => ExecuteSearch(alias, searchParams.Album, searchParams.Interactive, searchParams.TrackCount)));
                }
            }

            // Partial album title for long albums
            if (searchParams.Album?.Length > 20)
            {
                chain.AddTierFactory(SearchTierGenerator.CreateTier(() =>
                {
                    string[] albumWords = searchParams.Album.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    int halfLength = (int)Math.Ceiling(albumWords.Length / 2.0);
                    string halfAlbumTitle = string.Join(" ", albumWords.Take(halfLength));
                    return ExecuteSearch(searchParams.Artist, halfAlbumTitle, searchParams.Interactive, searchParams.TrackCount);
                }));
            }

            // Artist/Album only searches
            if (searchParams.Artist != null)
            {
                chain.AddTierFactory(SearchTierGenerator.CreateTier(
                    () => ExecuteSearch(searchParams.Artist, null, searchParams.Interactive, searchParams.TrackCount)));
            }

            if (searchParams.Album != null)
            {
                chain.AddTierFactory(SearchTierGenerator.CreateTier(
                    () => ExecuteSearch(null, searchParams.Album, searchParams.Interactive, searchParams.TrackCount)));
            }

            // Track fallback searches (limit to 4)
            if (Settings.UseTrackFallback)
            {
                int trackLimit = Math.Min(4, searchParams.Tracks.Count);
                for (int i = 0; i < trackLimit; i++)
                {
                    string track = searchParams.Tracks[i].Trim();
                    chain.AddTierFactory(SearchTierGenerator.CreateTier(
                        () => ExecuteSearch(searchParams.Artist, searchParams.Album, searchParams.Interactive, searchParams.TrackCount, track)));
                }
            }
        }

        private IEnumerable<IndexerRequest> ExecuteSearch(string? artist, string? album, bool interactive, int trackCount, string? searchText = null)
        {
            if (string.IsNullOrEmpty(searchText))
                searchText = BuildSearchText(artist, album);

            if (string.IsNullOrWhiteSpace(searchText) || _processedSearches.Contains(searchText))
                return Enumerable.Empty<IndexerRequest>();

            _processedSearches.Add(searchText);
            _logger.Trace($"Added '{searchText}' to processed searches. Total processed: {_processedSearches.Count}");

            try
            {
                IndexerRequest? request = GetRequestsAsync(artist, album, interactive, trackCount, searchText).GetAwaiter().GetResult();
                if (request != null)
                {
                    _logger.Trace($"Successfully generated request for search: {searchText}");
                    return new[] { request };
                }
                else
                {
                    _logger.Trace($"GetRequestsAsync returned null for search: {searchText}");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error executing search: {searchText}");
            }

            return Enumerable.Empty<IndexerRequest>();
        }

        private IEnumerable<IndexerRequest> ExecuteVariousArtistsSearches(string album, string? year, bool interactive, int trackCount)
        {
            List<IndexerRequest> requests = new();

            requests.AddRange(ExecuteSearch(null, $"{album} {year}", interactive, trackCount));
            requests.AddRange(ExecuteSearch(null, album, interactive, trackCount));

            if (Settings.StripPunctuation)
            {
                string strippedAlbumWithYear = StripPunctuation($"{album} {year}");
                string strippedAlbum = StripPunctuation(album);

                if (!string.Equals(strippedAlbumWithYear, $"{album} {year}", StringComparison.OrdinalIgnoreCase))
                    requests.AddRange(ExecuteSearch(null, strippedAlbumWithYear, interactive, trackCount));

                if (!string.Equals(strippedAlbum, album, StringComparison.OrdinalIgnoreCase))
                    requests.AddRange(ExecuteSearch(null, strippedAlbum, interactive, trackCount));
            }

            return requests;
        }

        private IEnumerable<IndexerRequest> ExecuteVariationSearches(string? artist, IEnumerable<string> variations, bool interactive, int trackCount)
        {
            List<IndexerRequest> requests = new();

            foreach (string variation in variations)
            {
                requests.AddRange(ExecuteSearch(artist, variation, interactive, trackCount));

                if (Settings.StripPunctuation)
                {
                    string strippedVariation = StripPunctuation(variation);
                    if (!string.Equals(strippedVariation, variation, StringComparison.OrdinalIgnoreCase))
                        requests.AddRange(ExecuteSearch(artist, strippedVariation, interactive, trackCount));
                }
            }

            return requests;
        }

        private async Task<IndexerRequest?> GetRequestsAsync(string? artist, string? album, bool interactive, int trackCount, string? searchText = null)
        {
            try
            {
                if (string.IsNullOrEmpty(searchText))
                    searchText = BuildSearchText(artist, album);

                _logger.Debug($"Search: {searchText}");

                dynamic searchData = CreateSearchData(searchText);
                dynamic searchId = searchData.Id;
                dynamic searchRequest = CreateSearchRequest(searchData);

                await ExecuteSearchAsync(searchRequest, searchId);

                dynamic request = CreateResultRequest(searchId, artist, album, interactive, trackCount);
                return new IndexerRequest(request);
            }
            catch (HttpException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.Warn($"Search request failed for artist: {artist}, album: {album}. Error: {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error generating search request for artist: {artist}, album: {album}");
                return null;
            }
        }

        private dynamic CreateSearchData(string searchText) => new
        {
            Id = Guid.NewGuid().ToString(),
            Settings.FileLimit,
            FilterResponses = true,
            Settings.MaximumPeerQueueLength,
            Settings.MinimumPeerUploadSpeed,
            Settings.MinimumResponseFileCount,
            Settings.ResponseLimit,
            SearchText = searchText,
            SearchTimeout = (int)(Settings.TimeoutInSeconds * 1000),
        };

        private HttpRequest CreateSearchRequest(dynamic searchData)
        {
            HttpRequest searchRequest = new HttpRequestBuilder($"{Settings.BaseUrl}/api/v0/searches")
                .SetHeader("X-API-KEY", Settings.ApiKey)
                .SetHeader("Content-Type", "application/json")
                .Post()
                .Build();

            searchRequest.SetContent(JsonConvert.SerializeObject(searchData));
            return searchRequest;
        }

        private async Task ExecuteSearchAsync(HttpRequest searchRequest, string searchId)
        {
            await _client.ExecuteAsync(searchRequest);
            await WaitOnSearchCompletionAsync(searchId, TimeSpan.FromSeconds(Settings.TimeoutInSeconds));
        }

        private HttpRequest CreateResultRequest(string searchId, string? artist, string? album, bool interactive, int trackCount)
        {
            HttpRequest request = new HttpRequestBuilder($"{Settings.BaseUrl}/api/v0/searches/{searchId}")
                .AddQueryParam("includeResponses", true)
                .SetHeader("X-API-KEY", Settings.ApiKey)
                .Build();

            request.ContentSummary = new
            {
                Album = album ?? "",
                Artist = artist,
                Interactive = interactive,
                MimimumFiles = Math.Max(Settings.MinimumResponseFileCount, Settings.FilterLessFilesThanAlbum ? trackCount : 1)
            }.ToJson();

            return request;
        }

        private async Task WaitOnSearchCompletionAsync(string searchId, TimeSpan timeout)
        {
            DateTime startTime = DateTime.UtcNow.AddSeconds(2);
            string state = "InProgress";
            int totalFilesFound = 0;
            bool hasTimedOut = false;
            DateTime timeoutEndTime = DateTime.UtcNow;

            while (state == "InProgress")
            {
                TimeSpan elapsed = DateTime.UtcNow - startTime;

                if (elapsed > timeout && !hasTimedOut)
                {
                    hasTimedOut = true;
                    timeoutEndTime = DateTime.UtcNow.AddSeconds(20);
                }
                else if (hasTimedOut && timeoutEndTime < DateTime.UtcNow)
                    break;

                dynamic? searchStatus = await GetSearchResultsAsync(searchId);
                state = searchStatus?.state ?? "InProgress";

                int fileCount = (int)(searchStatus?.fileCount ?? 0);
                if (fileCount > totalFilesFound)
                    totalFilesFound = fileCount;

                double progress = Math.Clamp(fileCount / (double)Settings.FileLimit, 0.0, 1.0);
                double delay = hasTimedOut && DateTime.UtcNow < timeoutEndTime ? 1.0 : CalculateQuadraticDelay(progress);

                await Task.Delay(TimeSpan.FromSeconds(delay));
                if (state != "InProgress")
                    break;
            }
        }

        private async Task<dynamic?> GetSearchResultsAsync(string searchId)
        {
            HttpRequest searchResultsRequest = new HttpRequestBuilder($"{Settings.BaseUrl}/api/v0/searches/{searchId}")
                     .SetHeader("X-API-KEY", Settings.ApiKey).Build();

            HttpResponse response = await _client.ExecuteAsync(searchResultsRequest);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                _logger.Warn($"Failed to fetch search results for ID {searchId}. Status: {response.StatusCode}, Content: {response.Content}");
                return null;
            }

            return JsonConvert.DeserializeObject<dynamic>(response.Content);
        }

        private static double CalculateQuadraticDelay(double progress)
        {
            const double a = 16;
            const double b = -16;
            const double c = 5;

            double delay = (a * Math.Pow(progress, 2)) + (b * progress) + c;
            return Math.Clamp(delay, 0.5, 5);
        }

        private static string BuildSearchText(string? artist, string? album) => string.Join(" ", new[] { album, artist }.Where(term => !string.IsNullOrWhiteSpace(term)).Select(term => term?.Trim()));

        private static bool ShouldNormalizeCharacters(string? artist, string? album)
        {
            string? normalizedArtist = artist != null ? NormalizeSpecialCharacters(artist) : null;
            string? normalizedAlbum = album != null ? NormalizeSpecialCharacters(album) : null;
            return (normalizedArtist != null && !string.Equals(normalizedArtist, artist, StringComparison.OrdinalIgnoreCase)) ||
                   (normalizedAlbum != null && !string.Equals(normalizedAlbum, album, StringComparison.OrdinalIgnoreCase));
        }

        private static bool ShouldStripPunctuation(string? artist, string? album)
        {
            string? strippedArtist = artist != null ? StripPunctuation(artist) : null;
            string? strippedAlbum = album != null ? StripPunctuation(album) : null;
            return (strippedArtist != null && !string.Equals(strippedArtist, artist, StringComparison.OrdinalIgnoreCase)) ||
                   (strippedAlbum != null && !string.Equals(strippedAlbum, album, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsVariousArtists(string artist) => artist.Equals("Various Artists", StringComparison.OrdinalIgnoreCase) || artist.Equals("VA", StringComparison.OrdinalIgnoreCase);

        private static bool ContainsVolumeReference(string album) => album.Contains("Volume", StringComparison.OrdinalIgnoreCase) || album.Contains("Vol", StringComparison.OrdinalIgnoreCase);

        private static bool ShouldGenerateRomanVariations(string album)
        {
            Match romanMatch = RomanNumeralPattern.Match(album);
            if (!romanMatch.Success) return false;

            Match volumeMatch = VolumePattern.Match(album);
            return !(volumeMatch.Success && volumeMatch.Groups[2].Value.Equals(romanMatch.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
        }

        private bool AnyEnhancedSearchEnabled() => Settings.UseFallbackSearch || Settings.NormalizeSpecialCharacters || Settings.StripPunctuation || Settings.HandleVariousArtists || Settings.HandleVolumeVariations;

        private static string StripPunctuation(string? input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;

            string stripped = PunctuationPattern.Replace(input, "");
            return Regex.Replace(stripped, @"\s+", " ").Trim();
        }

        private static string NormalizeSpecialCharacters(string? input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;

            string decomposed = input.Normalize(NormalizationForm.FormD);
            StringBuilder sb = new(decomposed.Length);

            foreach (char c in decomposed)
            {
                UnicodeCategory cat = CharUnicodeInfo.GetUnicodeCategory(c);
                if (cat != UnicodeCategory.NonSpacingMark && cat != UnicodeCategory.SpacingCombiningMark && cat != UnicodeCategory.EnclosingMark)
                    sb.Append(c);
            }

            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        private static IEnumerable<string> GenerateVolumeVariations(string album)
        {
            if (string.IsNullOrEmpty(album)) yield break;

            Match volumeMatch = VolumePattern.Match(album);
            if (!volumeMatch.Success) yield break;

            string volumeFormat = volumeMatch.Groups[1].Value;
            string volumeNumber = volumeMatch.Groups[2].Value;

            if (RomanNumerals.TryGetValue(volumeNumber, out int arabicNumber))
            {
                yield return album.Replace(volumeMatch.Value, $"{volumeFormat} {arabicNumber}");
            }
            else if (int.TryParse(volumeNumber, out arabicNumber) && arabicNumber > 0 && arabicNumber <= 20)
            {
                KeyValuePair<string, int> romanPair = RomanNumerals.FirstOrDefault(x => x.Value == arabicNumber);
                if (!string.IsNullOrEmpty(romanPair.Key))
                    yield return album.Replace(volumeMatch.Value, $"{volumeFormat} {romanPair.Key}");
            }
            foreach (string format in VolumeFormats)
            {
                if (!format.Equals(volumeFormat, StringComparison.OrdinalIgnoreCase))
                    yield return album.Replace(volumeMatch.Value, $"{format} {volumeNumber}");
            }
            if (album.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 3)
            {
                string withoutVolume = album.Replace(volumeMatch.Value, "").Trim();
                if (withoutVolume.Length > 10)
                    yield return withoutVolume;
            }
        }

        private static IEnumerable<string> GenerateRomanNumeralVariations(string album)
        {
            if (string.IsNullOrEmpty(album)) yield break;

            Match romanMatch = RomanNumeralPattern.Match(album);
            if (!romanMatch.Success) yield break;
            Match volumeMatch = VolumePattern.Match(album);
            if (volumeMatch.Success && volumeMatch.Groups[2].Value.Equals(romanMatch.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
                yield break;

            string romanNumeral = romanMatch.Groups[1].Value;
            if (RomanNumerals.TryGetValue(romanNumeral, out int arabicNumber))
                yield return album.Replace(romanMatch.Value, arabicNumber.ToString());
        }

        private record SearchParameters(string? Artist, string? Album, string? Year, bool Interactive, int TrackCount, List<string> Aliases, List<string> Tracks);
    }
}