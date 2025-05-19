using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Indexers.Soulseek
{
    internal class SlskdRequestGenerator : IIndexerRequestGenerator<ExtendedIndexerPageableRequest>
    {
        private readonly SlskdIndexer _indexer;
        private readonly Logger _logger;
        private SlskdSettings Settings => _indexer.Settings;
        private readonly IHttpClient _client;

        private HttpRequest? _searchResultsRequest;

        // Track already processed searches to prevent duplicates
        private readonly HashSet<string> _processedSearches = new(StringComparer.OrdinalIgnoreCase);

        // Dictionary of Roman numerals for conversions
        private static readonly Dictionary<string, int> RomanNumerals = new(StringComparer.OrdinalIgnoreCase)
        {
            { "I", 1 }, { "II", 2 }, { "III", 3 }, { "IV", 4 }, { "V", 5 },
            { "VI", 6 }, { "VII", 7 }, { "VIII", 8 }, { "IX", 9 }, { "X", 10 },
            { "XI", 11 }, { "XII", 12 }, { "XIII", 13 }, { "XIV", 14 }, { "XV", 15 },
            { "XVI", 16 }, { "XVII", 17 }, { "XVIII", 18 }, { "XIX", 19 }, { "XX", 20 }
        };

        // Volume variations for replacement
        private static readonly string[] VolumeFormats = { "Volume", "Vol.", "Vol", "v", "V" };

        // Static regex patterns
        private static readonly Regex PunctuationPattern = new(@"[^\w\s-&]", RegexOptions.Compiled);
        private static readonly Regex VolumePattern = new(@"(Vol(?:ume)?\.?)\s*([0-9]+|[IVXLCDM]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RomanNumeralPattern = new(@"\b([IVXLCDM]+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public SlskdRequestGenerator(SlskdIndexer indexer, IHttpClient client)
        {
            _indexer = indexer;
            _client = client;
            _logger = NzbDroneLogger.GetLogger(this);
        }

        public IndexerPageableRequestChain<ExtendedIndexerPageableRequest> GetRecentRequests() => new ExtendedIndexerPageableRequestChain(Settings.MinimumResults);

        public IndexerPageableRequestChain<ExtendedIndexerPageableRequest> GetSearchRequests(AlbumSearchCriteria searchCriteria)
        {
            _logger.Trace($"Generating search requests for album: {searchCriteria.AlbumQuery} by artist: {searchCriteria.ArtistQuery}");
            int trackCount = searchCriteria.Albums.FirstOrDefault()?.AlbumReleases.Value.Min(x => x.TrackCount) ?? 0;
            _processedSearches.Clear();

            ExtendedIndexerPageableRequestChain chain = new(Settings.MinimumResults);

            AddSearchTiersToChain(
                chain,
                searchCriteria.ArtistQuery,
                searchCriteria.AlbumYear.ToString(),
                searchCriteria.ArtistQuery != searchCriteria.AlbumQuery ? searchCriteria.AlbumQuery : null,
                searchCriteria.InteractiveSearch,
                trackCount,
                searchCriteria.Artist?.Metadata.Value.Aliases ?? new List<string>());

            return chain;
        }

        public IndexerPageableRequestChain<ExtendedIndexerPageableRequest> GetSearchRequests(ArtistSearchCriteria searchCriteria)
        {
            _logger.Debug($"Generating search requests for artist: {searchCriteria.CleanArtistQuery}");
            int trackCount = searchCriteria.Albums.FirstOrDefault()?.AlbumReleases.Value.Min(x => x.TrackCount) ?? 0;
            _processedSearches.Clear();

            ExtendedIndexerPageableRequestChain chain = new(Settings.MinimumResults);

            AddSearchTiersToChain(
                chain,
                searchCriteria.CleanArtistQuery,
                null,
                null,
                searchCriteria.InteractiveSearch,
                trackCount,
                searchCriteria.Artist?.Metadata.Value.Aliases ?? new List<string>());

            return chain;
        }

        private void AddSearchTiersToChain(ExtendedIndexerPageableRequestChain chain, string? artist, string? album, string? year, bool interactive, int trackCount, List<string> aliases)
        {
            _logger.Trace("Adding Tier 1: Base search with original terms");
            chain.AddTier(DeferredGetRequests(artist, album, interactive, trackCount));

            if (!AnyEnhancedSearchEnabled())
                return;

            AddCharacterNormalizationTierIfEnabled(chain, artist, album, interactive, trackCount);

            AddPunctuationStrippingTierIfEnabled(chain, artist, album, interactive, trackCount);

            AddVariousArtistsTierIfEnabled(chain, artist, album, year, interactive, trackCount);

            AddVolumeVariationsTierIfEnabled(chain, artist, album, interactive, trackCount);

            AddRomanNumeralVariationsTierIfEnabled(chain, artist, album, interactive, trackCount);

            AddFallbackSearchTiersIfEnabled(chain, artist, album, interactive, trackCount, aliases);
        }

        private bool AnyEnhancedSearchEnabled() =>
            Settings.UseFallbackSearch ||
            Settings.NormalizeSpecialCharacters ||
            Settings.StripPunctuation ||
            Settings.HandleVariousArtists ||
            Settings.HandleVolumeVariations;

        private void AddCharacterNormalizationTierIfEnabled(ExtendedIndexerPageableRequestChain chain, string? artist, string? album, bool interactive, int trackCount)
        {
            if (!Settings.NormalizeSpecialCharacters)
                return;

            string? normalizedArtist = artist != null ? NormalizeSpecialCharacters(artist) : null;
            string? normalizedAlbum = album != null ? NormalizeSpecialCharacters(album) : null;

            if ((normalizedArtist != null && !string.Equals(normalizedArtist, artist, StringComparison.OrdinalIgnoreCase)) ||
                (normalizedAlbum != null && !string.Equals(normalizedAlbum, album, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.Trace("Adding Tier: Special character normalization");
                chain.AddTier(DeferredGetRequests(normalizedArtist, normalizedAlbum, interactive, trackCount));
            }
        }

        private void AddPunctuationStrippingTierIfEnabled(ExtendedIndexerPageableRequestChain chain, string? artist, string? album, bool interactive, int trackCount)
        {
            if (!Settings.StripPunctuation)
                return;

            string? strippedArtist = artist != null ? StripPunctuation(artist) : null;
            string? strippedAlbum = album != null ? StripPunctuation(album) : null;

            if ((strippedArtist != null && !string.Equals(strippedArtist, artist, StringComparison.OrdinalIgnoreCase)) ||
                (strippedAlbum != null && !string.Equals(strippedAlbum, album, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.Trace("Adding Tier: Strip punctuation");
                chain.AddTier(DeferredGetRequests(strippedArtist, strippedAlbum, interactive, trackCount));

                if (Settings.NormalizeSpecialCharacters)
                    AddCombinedNormalizationAndStrippingTier(chain, strippedArtist, strippedAlbum, interactive, trackCount);
            }
        }

        private void AddCombinedNormalizationAndStrippingTier(ExtendedIndexerPageableRequestChain chain, string? strippedArtist,
            string? strippedAlbum, bool interactive, int trackCount)
        {
            string? normalizedStrippedArtist = strippedArtist != null ?
                NormalizeSpecialCharacters(strippedArtist) : null;

            string? normalizedStrippedAlbum = strippedAlbum != null ?
                NormalizeSpecialCharacters(strippedAlbum) : null;

            if ((normalizedStrippedArtist != null && !string.Equals(normalizedStrippedArtist, strippedArtist, StringComparison.OrdinalIgnoreCase)) ||
                (normalizedStrippedAlbum != null && !string.Equals(normalizedStrippedAlbum, strippedAlbum, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.Trace("Adding Tier: Normalized + Stripped");
                chain.AddTier(DeferredGetRequests(normalizedStrippedArtist, normalizedStrippedAlbum, interactive, trackCount));
            }
        }

        private void AddVariousArtistsTierIfEnabled(ExtendedIndexerPageableRequestChain chain, string? artist, string? album, string? year, bool interactive, int trackCount)
        {
            if (!Settings.HandleVariousArtists || artist == null || album == null)
                return;

            bool isVariousArtists = artist.Equals("Various Artists", StringComparison.OrdinalIgnoreCase) ||
                                    artist.Equals("VA", StringComparison.OrdinalIgnoreCase);

            if (!isVariousArtists)
                return;

            _logger.Trace("Adding Tier: Various Artists handling - search by album only");
            chain.AddTier(DeferredGetRequests(null, $"{album} {year}", interactive, trackCount));
            chain.AddTier(DeferredGetRequests(null, album, interactive, trackCount));

            if (Settings.StripPunctuation)
            {
                AddStrippedVariousArtistsTier(chain, $"{album} {year}", interactive, trackCount);
                AddStrippedVariousArtistsTier(chain, album, interactive, trackCount);
            }
        }

        private void AddStrippedVariousArtistsTier(ExtendedIndexerPageableRequestChain chain, string album, bool interactive, int trackCount)
        {
            string strippedAlbum = StripPunctuation(album);
            if (!string.Equals(strippedAlbum, album, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Trace("Adding Tier: Various Artists with stripped album");
                chain.AddTier(DeferredGetRequests(null, strippedAlbum, interactive, trackCount));
            }
        }

        private void AddVolumeVariationsTierIfEnabled(ExtendedIndexerPageableRequestChain chain, string? artist, string? album, bool interactive, int trackCount)
        {
            if (!Settings.HandleVolumeVariations || album == null)
                return;

            bool containsVolumeReference = album.Contains("Volume", StringComparison.OrdinalIgnoreCase) ||
                                           album.Contains("Vol", StringComparison.OrdinalIgnoreCase);

            if (!containsVolumeReference)
                return;

            _logger.Trace("Adding Tier: Volume variations");
            foreach (string variation in GenerateVolumeVariations(album))
            {
                chain.AddTier(DeferredGetRequests(artist, variation, interactive, trackCount));
                if (Settings.StripPunctuation)
                    AddStrippedVolumeVariationTier(chain, artist, variation, interactive, trackCount);
            }
        }

        private void AddRomanNumeralVariationsTierIfEnabled(ExtendedIndexerPageableRequestChain chain, string? artist, string? album, bool interactive, int trackCount)
        {
            if (!Settings.HandleVolumeVariations || album == null)
                return;
            Match romanMatch = RomanNumeralPattern.Match(album);
            if (!romanMatch.Success)
                return;
            Match volumeMatch = VolumePattern.Match(album);
            if (volumeMatch.Success && volumeMatch.Groups[2].Value.Equals(romanMatch.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
                return;

            _logger.Trace("Adding Tier: Roman numeral variations");
            foreach (string variation in GenerateRomanNumeralVariations(album))
            {
                chain.AddTier(DeferredGetRequests(artist, variation, interactive, trackCount));
                if (Settings.StripPunctuation)
                    AddStrippedRomanVariationTier(chain, artist, variation, interactive, trackCount);
            }
        }

        private void AddStrippedVolumeVariationTier(ExtendedIndexerPageableRequestChain chain, string? artist, string variation, bool interactive, int trackCount)
        {
            string strippedVariation = StripPunctuation(variation);
            if (!string.Equals(strippedVariation, variation, StringComparison.OrdinalIgnoreCase))
                chain.AddTier(DeferredGetRequests(artist, strippedVariation, interactive, trackCount));
        }

        private void AddStrippedRomanVariationTier(ExtendedIndexerPageableRequestChain chain, string? artist, string variation, bool interactive, int trackCount)
        {
            string strippedVariation = StripPunctuation(variation);
            if (!string.Equals(strippedVariation, variation, StringComparison.OrdinalIgnoreCase))
                chain.AddTier(DeferredGetRequests(artist, strippedVariation, interactive, trackCount));
        }

        private void AddFallbackSearchTiersIfEnabled(ExtendedIndexerPageableRequestChain chain, string? artist, string? album, bool interactive, int trackCount, List<string> aliases)
        {
            if (!Settings.UseFallbackSearch)
                return;

            _logger.Trace("Adding Tier: Existing fallback search logic");

            AddAliasTiers(chain, aliases, album, interactive, trackCount);

            if (album?.Length > 20)
                AddPartialAlbumTitleTier(chain, artist, album, interactive, trackCount);

            if (artist != null)
                chain.AddTier(DeferredGetRequests(artist, null, interactive, trackCount));

            if (album != null)
                chain.AddTier(DeferredGetRequests(null, album, interactive, trackCount));
        }

        private void AddAliasTiers(ExtendedIndexerPageableRequestChain chain, List<string> aliases, string? album, bool interactive, int trackCount)
        {
            for (int i = 0; i < 2 && i < aliases.Count; i++)
                if (aliases[i].Length > 3)
                    chain.AddTier(DeferredGetRequests(aliases[i], album, interactive, trackCount));
        }

        private void AddPartialAlbumTitleTier(ExtendedIndexerPageableRequestChain chain, string? artist, string album, bool interactive, int trackCount)
        {
            string[] albumWords = album.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int halfLength = (int)Math.Ceiling(albumWords.Length / 2.0);
            string halfAlbumTitle = string.Join(" ", albumWords.Take(halfLength));

            chain.AddTier(DeferredGetRequests(artist, halfAlbumTitle, interactive, trackCount));
        }

        private IEnumerable<IndexerRequest> DeferredGetRequests(string? artist, string? album, bool interactive, int trackCount, string? fullAlbum = null)
        {
            _searchResultsRequest = null;

            string searchKey = BuildSearchText(artist, album);
            if (!string.IsNullOrWhiteSpace(searchKey) && _processedSearches.Contains(searchKey))
            {
                _logger.Trace($"Skipping duplicate search: {searchKey}");
                yield break;
            }

            _processedSearches.Add(searchKey);

            IndexerRequest? request = GetRequestsAsync(artist, album, interactive, trackCount, fullAlbum).Result;

            if (request != null)
                yield return request;
        }

        private async Task<IndexerRequest?> GetRequestsAsync(string? artist, string? album, bool interactive, int trackCount, string? fullAlbum = null)
        {
            try
            {
                string searchText = BuildSearchText(artist, album);

                _logger.Debug($"Search: {searchText}");

                dynamic searchData = CreateSearchData(searchText);
                dynamic searchRequest = CreateSearchRequest(searchData);

                await ExecuteSearchAsync(searchRequest, searchData.Id);

                dynamic request = CreateResultRequest(searchData.Id, artist, album, fullAlbum, interactive, trackCount);
                return new IndexerRequest(request);
            }
            catch (HttpException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.Warn($"Search request failed for artist: {artist}, album: {album}. Error: {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"An error occurred while generating search request for artist: {artist}, album: {album}");
                return null;
            }
        }

        private static string BuildSearchText(string? artist, string? album) => string.Join(" ", new[] { album, artist }
                .Where(term => !string.IsNullOrWhiteSpace(term))
                .Select(term => term?.Trim()));

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
            _logger.Trace($"Generated search initiation request: {searchRequest.Url}");
        }

        private HttpRequest CreateResultRequest(string searchId, string? artist, string? album, string? fullAlbum, bool interactive, int trackCount)
        {
            HttpRequest request = new HttpRequestBuilder($"{Settings.BaseUrl}/api/v0/searches/{searchId}")
                .AddQueryParam("includeResponses", true)
                .SetHeader("X-API-KEY", Settings.ApiKey)
                .Build();

            request.ContentSummary = new
            {
                Album = fullAlbum ?? album ?? "",
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
                double delay = CalculateDelay(hasTimedOut, timeoutEndTime, progress);

                await Task.Delay(TimeSpan.FromSeconds(delay));
                if (state != "InProgress")
                    break;
            }

            _logger.Trace($"Search completed with state: {state}, Total files found: {totalFilesFound}");
        }

        private static double CalculateDelay(bool hasTimedOut, DateTime timeoutEndTime, double progress)
        {
            if (hasTimedOut && DateTime.UtcNow < timeoutEndTime)
                return 1;
            else
                return CalculateQuadraticDelay(progress);
        }

        private static double CalculateQuadraticDelay(double progress)
        {
            const double a = 16;
            const double b = -16;
            const double c = 5;

            double delay = (a * Math.Pow(progress, 2)) + (b * progress) + c;
            return Math.Clamp(delay, 0.5, 5);
        }

        private async Task<dynamic?> GetSearchResultsAsync(string searchId)
        {
            _searchResultsRequest ??= new HttpRequestBuilder($"{Settings.BaseUrl}/api/v0/searches/{searchId}")
                     .SetHeader("X-API-KEY", Settings.ApiKey).Build();

            HttpResponse response = await _client.ExecuteAsync(_searchResultsRequest);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                _logger.Warn($"Failed to fetch search results. Status: {response.StatusCode}, Content: {response.Content}");
                return null;
            }

            return JsonConvert.DeserializeObject<dynamic>(response.Content);
        }

        private string StripPunctuation(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            string stripped = PunctuationPattern.Replace(input, "");
            string result = Regex.Replace(stripped, @"\s+", " ").Trim();

            _logger.Trace($"Stripped punctuation: '{input}' -> '{result}'");
            return result;
        }

        private string NormalizeSpecialCharacters(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            string decomposed = input.Normalize(NormalizationForm.FormD);
            StringBuilder sb = new(decomposed.Length);

            foreach (char c in decomposed)
            {
                UnicodeCategory cat = CharUnicodeInfo.GetUnicodeCategory(c);
                if (cat != UnicodeCategory.NonSpacingMark &&
                    cat != UnicodeCategory.SpacingCombiningMark &&
                    cat != UnicodeCategory.EnclosingMark)
                {
                    sb.Append(c);
                }
            }

            string result = sb.ToString().Normalize(NormalizationForm.FormC);
            _logger.Trace($"Normalized special characters: '{input}' -> '{result}'");
            return result;
        }

        private static IEnumerable<string> GenerateVolumeVariations(string album)
        {
            if (string.IsNullOrEmpty(album))
                yield break;

            Match volumeMatch = VolumePattern.Match(album);
            if (!volumeMatch.Success)
                yield break;

            string volumeFormat = volumeMatch.Groups[1].Value; // e.g. "Volume", "Vol.", etc.
            string volumeNumber = volumeMatch.Groups[2].Value; // e.g. "1", "IV", etc.

            foreach (string variation in GenerateNumeralVariations(album, volumeMatch, volumeFormat, volumeNumber))
                yield return variation;

            foreach (string variation in GenerateFormatVariations(album, volumeMatch, volumeFormat, volumeNumber))
                yield return variation;

            string? potentialShortenedVersion = GenerateShortenedVersion(album, volumeMatch);
            if (potentialShortenedVersion != null)
                yield return potentialShortenedVersion;
        }

        private static IEnumerable<string> GenerateRomanNumeralVariations(string album)
        {
            if (string.IsNullOrEmpty(album))
                yield break;

            Match romanMatch = RomanNumeralPattern.Match(album);
            if (!romanMatch.Success)
                yield break;

            string romanNumeral = romanMatch.Groups[1].Value;

            if (RomanNumerals.TryGetValue(romanNumeral, out int arabicNumber))
                yield return album.Replace(romanMatch.Value, arabicNumber.ToString());
        }

        private static IEnumerable<string> GenerateNumeralVariations(string album, Match volumeMatch, string volumeFormat, string volumeNumber)
        {
            if (RomanNumerals.TryGetValue(volumeNumber, out int arabicNumber))
            {
                yield return album.Replace(volumeMatch.Value, $"{volumeFormat} {arabicNumber}");
            }
            else if (int.TryParse(volumeNumber, out arabicNumber))
            {
                if (arabicNumber > 0 && arabicNumber <= 20 && RomanNumerals.ContainsValue(arabicNumber))
                {
                    string romanNumeral = RomanNumerals.First(x => x.Value == arabicNumber).Key;
                    yield return album.Replace(volumeMatch.Value, $"{volumeFormat} {romanNumeral}");
                }
            }
        }

        private static IEnumerable<string> GenerateFormatVariations(string album, Match volumeMatch, string volumeFormat, string volumeNumber)
        {
            foreach (string format in VolumeFormats)
            {
                if (!format.Equals(volumeFormat, StringComparison.OrdinalIgnoreCase))
                {
                    yield return album.Replace(volumeMatch.Value, $"{format} {volumeNumber}");
                }
            }

            if (volumeFormat.EndsWith("."))
            {
                string formatWithoutDot = volumeFormat.TrimEnd('.');
                yield return album.Replace(volumeMatch.Value, $"{formatWithoutDot}{volumeNumber}");
            }
            else
            {
                yield return album.Replace(volumeMatch.Value, $"{volumeFormat}{volumeNumber}");
            }
        }

        private static string? GenerateShortenedVersion(string album, Match volumeMatch)
        {
            if (album.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length > 3)
            {
                string albumWithoutVolume = Regex.Replace(album, volumeMatch.Value, "", RegexOptions.IgnoreCase).Trim();
                if (albumWithoutVolume.Length > 10)
                    return albumWithoutVolume;
            }
            return null;
        }
    }
}