using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Music;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tubifarry.Core.Replacements;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Indexers.Soulseek
{
    internal class SlskdRequestGenerator : IIndexerRequestGenerator<LazyIndexerPageableRequest>
    {
        private readonly SlskdIndexer _indexer;
        private readonly Logger _logger;
        private readonly IHttpClient _client;
        private readonly HashSet<string> _processedSearches = new(StringComparer.OrdinalIgnoreCase);

        private SlskdSettings Settings => _indexer.Settings;

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
                () => ExecuteSearch(searchParams.Artist, searchParams.Album, searchParams.Interactive, false, searchParams.TrackCount)));

            if (!AnyEnhancedSearchEnabled())
            {
                _logger.Trace("No enhanced search enabled, returning chain with base tier only");
                return chain;
            }

            // Tier 2: Character normalization
            if (Settings.NormalizeSpecialCharacters)
            {
                chain.AddTierFactory(SearchTierGenerator.CreateConditionalTier(
                    () => SlskdTextProcessor.ShouldNormalizeCharacters(searchParams.Artist, searchParams.Album),
                    () => ExecuteSearch(SlskdTextProcessor.NormalizeSpecialCharacters(searchParams.Artist), SlskdTextProcessor.NormalizeSpecialCharacters(searchParams.Album), searchParams.Interactive, false, searchParams.TrackCount)));
            }

            // Tier 3: Punctuation stripping
            if (Settings.StripPunctuation)
            {
                chain.AddTierFactory(SearchTierGenerator.CreateConditionalTier(
                    () => SlskdTextProcessor.ShouldStripPunctuation(searchParams.Artist, searchParams.Album),
                    () => ExecuteSearch(SlskdTextProcessor.StripPunctuation(searchParams.Artist), SlskdTextProcessor.StripPunctuation(searchParams.Album), searchParams.Interactive, false, searchParams.TrackCount)));

                if (Settings.NormalizeSpecialCharacters)
                {
                    chain.AddTierFactory(SearchTierGenerator.CreateConditionalTier(
                        () => SlskdTextProcessor.ShouldStripPunctuation(searchParams.Artist, searchParams.Album),
                        () => ExecuteSearch(SlskdTextProcessor.NormalizeSpecialCharacters(SlskdTextProcessor.StripPunctuation(searchParams.Artist)), SlskdTextProcessor.NormalizeSpecialCharacters(SlskdTextProcessor.StripPunctuation(searchParams.Album)), searchParams.Interactive, false, searchParams.TrackCount)));
                }
            }

            // Tier 4: Various artists handling
            if (Settings.HandleVariousArtists && searchParams.Artist != null && searchParams.Album != null)
            {
                chain.AddTierFactory(SearchTierGenerator.CreateConditionalTier(
                    () => SlskdTextProcessor.IsVariousArtists(searchParams.Artist),
                    () => ExecuteVariousArtistsSearches(searchParams.Album, searchParams.Year, searchParams.Interactive, searchParams.TrackCount)));
            }

            // Tier 5: Volume variations
            if (Settings.HandleVolumeVariations && searchParams.Album != null)
            {
                chain.AddTierFactory(SearchTierGenerator.CreateConditionalTier(
                    () => SlskdTextProcessor.ContainsVolumeReference(searchParams.Album),
                    () => ExecuteVariationSearches(searchParams.Artist, SlskdTextProcessor.GenerateVolumeVariations(searchParams.Album), searchParams.Interactive, searchParams.TrackCount)));

                chain.AddTierFactory(SearchTierGenerator.CreateConditionalTier(
                    () => SlskdTextProcessor.ShouldGenerateRomanVariations(searchParams.Album),
                    () => ExecuteVariationSearches(searchParams.Artist, SlskdTextProcessor.GenerateRomanNumeralVariations(searchParams.Album), searchParams.Interactive, searchParams.TrackCount)));
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
                        () => ExecuteSearch(alias, searchParams.Album, searchParams.Interactive, false, searchParams.TrackCount)));
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
                    return ExecuteSearch(searchParams.Artist, halfAlbumTitle, searchParams.Interactive, false, searchParams.TrackCount);
                }));
            }

            // Artist/Album only searches
            if (searchParams.Artist != null)
            {
                chain.AddTierFactory(SearchTierGenerator.CreateTier(
                    () => ExecuteSearch(searchParams.Artist, null, searchParams.Interactive, false, searchParams.TrackCount)));
            }

            if (searchParams.Album != null)
            {
                chain.AddTierFactory(SearchTierGenerator.CreateTier(
                    () => ExecuteSearch(null, searchParams.Album, searchParams.Interactive, false, searchParams.TrackCount)));
            }

            // Track fallback searches (limit to 4)
            if (Settings.UseTrackFallback)
            {
                int trackLimit = Math.Min(4, searchParams.Tracks.Count);
                for (int i = 0; i < trackLimit; i++)
                {
                    string track = searchParams.Tracks[i].Trim();
                    chain.AddTierFactory(SearchTierGenerator.CreateTier(
                        () => ExecuteSearch(searchParams.Artist, searchParams.Album, searchParams.Interactive, true, searchParams.TrackCount, track)));
                }
            }
        }

        private IEnumerable<IndexerRequest> ExecuteSearch(string? artist, string? album, bool interactive, bool expand, int trackCount, string? searchText = null)
        {
            if (string.IsNullOrEmpty(searchText))
                searchText = SlskdTextProcessor.BuildSearchText(artist, album);

            if (string.IsNullOrWhiteSpace(searchText) || _processedSearches.Contains(searchText))
                return Enumerable.Empty<IndexerRequest>();

            _processedSearches.Add(searchText);
            _logger.Trace($"Added '{searchText}' to processed searches. Total processed: {_processedSearches.Count}");

            try
            {
                IndexerRequest? request = GetRequestsAsync(artist, album, interactive, expand, trackCount, searchText).GetAwaiter().GetResult();
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

            requests.AddRange(ExecuteSearch(null, $"{album} {year}", interactive, false, trackCount));
            requests.AddRange(ExecuteSearch(null, album, interactive, false, trackCount));

            if (Settings.StripPunctuation)
            {
                string strippedAlbumWithYear = SlskdTextProcessor.StripPunctuation($"{album} {year}");
                string strippedAlbum = SlskdTextProcessor.StripPunctuation(album);

                if (!string.Equals(strippedAlbumWithYear, $"{album} {year}", StringComparison.OrdinalIgnoreCase))
                    requests.AddRange(ExecuteSearch(null, strippedAlbumWithYear, interactive, false, trackCount));

                if (!string.Equals(strippedAlbum, album, StringComparison.OrdinalIgnoreCase))
                    requests.AddRange(ExecuteSearch(null, strippedAlbum, interactive, false, trackCount));
            }

            return requests;
        }

        private IEnumerable<IndexerRequest> ExecuteVariationSearches(string? artist, IEnumerable<string> variations, bool interactive, int trackCount)
        {
            List<IndexerRequest> requests = new();

            foreach (string variation in variations)
            {
                requests.AddRange(ExecuteSearch(artist, variation, interactive, false, trackCount));

                if (Settings.StripPunctuation)
                {
                    string strippedVariation = SlskdTextProcessor.StripPunctuation(variation);
                    if (!string.Equals(strippedVariation, variation, StringComparison.OrdinalIgnoreCase))
                        requests.AddRange(ExecuteSearch(artist, strippedVariation, interactive, false, trackCount));
                }
            }

            return requests;
        }

        private async Task<IndexerRequest?> GetRequestsAsync(string? artist, string? album, bool interactive, bool expand, int trackCount, string? searchText = null)
        {
            try
            {
                if (string.IsNullOrEmpty(searchText))
                    searchText = SlskdTextProcessor.BuildSearchText(artist, album);

                _logger.Debug($"Search: {searchText}");

                dynamic searchData = CreateSearchData(searchText);
                dynamic searchId = searchData.Id;
                dynamic searchRequest = CreateSearchRequest(searchData);

                await ExecuteSearchAsync(searchRequest, searchId);

                dynamic request = CreateResultRequest(searchId, artist, album, interactive, expand, trackCount);
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

            searchRequest.SetContent(JsonSerializer.Serialize(searchData));
            return searchRequest;
        }

        private async Task ExecuteSearchAsync(HttpRequest searchRequest, string searchId)
        {
            await _client.ExecuteAsync(searchRequest);
            await WaitOnSearchCompletionAsync(searchId, TimeSpan.FromSeconds(Settings.TimeoutInSeconds));
        }

        private HttpRequest CreateResultRequest(string searchId, string? artist, string? album, bool interactive, bool expand, int trackCount)
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
                ExpandDirectory = expand,
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

                JsonNode? searchStatus = await GetSearchResultsAsync(searchId);

                state = searchStatus?["state"]?.GetValue<string>() ?? "InProgress";
                int fileCount = searchStatus?["fileCount"]?.GetValue<int>() ?? 0;

                if (fileCount > totalFilesFound)
                    totalFilesFound = fileCount;

                double progress = Math.Clamp(fileCount / (double)Settings.FileLimit, 0.0, 1.0);
                double delay = hasTimedOut && DateTime.UtcNow < timeoutEndTime ? 1.0 : CalculateQuadraticDelay(progress);

                await Task.Delay(TimeSpan.FromSeconds(delay));
                if (state != "InProgress")
                    break;
            }
        }

        private async Task<JsonNode?> GetSearchResultsAsync(string searchId)
        {
            HttpRequest searchResultsRequest = new HttpRequestBuilder($"{Settings.BaseUrl}/api/v0/searches/{searchId}")
                     .SetHeader("X-API-KEY", Settings.ApiKey).Build();

            HttpResponse response = await _client.ExecuteAsync(searchResultsRequest);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                _logger.Warn($"Failed to fetch search results for ID {searchId}. Status: {response.StatusCode}, Content: {response.Content}");
                return null;
            }

            return JsonSerializer.Deserialize<JsonNode>(response.Content);
        }

        private static double CalculateQuadraticDelay(double progress)
        {
            const double a = 16;
            const double b = -16;
            const double c = 5;

            double delay = (a * Math.Pow(progress, 2)) + (b * progress) + c;
            return Math.Clamp(delay, 0.5, 5);
        }

        private bool AnyEnhancedSearchEnabled() => Settings.UseFallbackSearch || Settings.NormalizeSpecialCharacters || Settings.StripPunctuation || Settings.HandleVariousArtists || Settings.HandleVolumeVariations;

        public async Task<IGrouping<string, SlskdFileData>?> ExpandDirectory(string username, string directoryPath, SlskdFileData originalTrack)
        {
            try
            {
                HttpRequest request = new HttpRequestBuilder($"{Settings.BaseUrl}/api/v0/users/{Uri.EscapeDataString(username)}/directory")
                    .SetHeader("X-API-KEY", Settings.ApiKey)
                    .SetHeader("Content-Type", "application/json")
                    .Post()
                    .Build();

                request.SetContent(JsonSerializer.Serialize(new { directory = directoryPath }));

                HttpResponse response = await _client.ExecuteAsync(request);

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    SlskdDirectoryApiResponse[]? directoryResponse = JsonSerializer.Deserialize<SlskdDirectoryApiResponse[]>(response.Content,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (directoryResponse?.Length > 0 && directoryResponse[0].Files?.Any() == true)
                    {
                        string originalExtension = originalTrack.Extension?.ToLowerInvariant() ?? "";

                        List<SlskdFileData> directoryFiles = directoryResponse[0].Files
                            .Where(f => AudioFormatHelper.GetAudioCodecFromExtension(Path.GetExtension(f.Filename)) != AudioFormat.Unknown)
                            .Select(f =>
                            {
                                string fileExtension = Path.GetExtension(f.Filename)?.TrimStart('.').ToLowerInvariant() ?? "";
                                bool sameExtension = fileExtension == originalExtension;

                                return new SlskdFileData(
                                    Filename: $"{directoryPath}\\{f.Filename}",
                                    BitRate: sameExtension ? originalTrack.BitRate : null,
                                    BitDepth: sameExtension ? originalTrack.BitDepth : null,
                                    Size: f.Size,
                                    Length: sameExtension ? originalTrack.Length : null,
                                    Extension: fileExtension,
                                    SampleRate: sameExtension ? originalTrack.SampleRate : null,
                                    Code: f.Code,
                                    IsLocked: false
                                );
                            }).ToList();

                        if (directoryFiles.Any())
                        {
                            return directoryFiles.GroupBy(f => SlskdTextProcessor.GetDirectoryFromFilename(f.Filename)).First();
                        }
                    }
                }
                else
                {
                    _logger.Debug($"Directory API returned {response.StatusCode} for {username}:{directoryPath}");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error expanding directory {username}:{directoryPath}");
            }

            return null;
        }

        private record SearchParameters(string? Artist, string? Album, string? Year, bool Interactive, int TrackCount, List<string> Aliases, List<string> Tracks);
    }
}