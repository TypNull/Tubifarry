using FuzzySharp;
using NLog;
using Tubifarry.Core.Model;
using Tubifarry.Core.Records;
using Tubifarry.Core.Utilities;
using Tubifarry.Download.Clients.YouTube;
using YouTubeMusicAPI.Client;
using YouTubeMusicAPI.Models.Info;
using YouTubeMusicAPI.Models.Search;
using YouTubeMusicAPI.Models.Streaming;
using YouTubeMusicAPI.Pagination;

namespace Tubifarry.Indexers.Spotify
{
    public interface ISpotifyToYouTubeEnricher
    {
        void UpdateSettings(SpotifyIndexerSettings settings);
        List<AlbumData> EnrichWithYouTubeData(List<AlbumData> albums);
        Task<AlbumData?> EnrichSingleAlbumAsync(AlbumData albumData);
    }

    /// <summary>
    /// Enriches Spotify album data with YouTube Music streaming information.
    /// </summary>
    internal class SpotifyToYouTubeEnricher : ISpotifyToYouTubeEnricher
    {
        private const int DEFAULT_BITRATE = 128;
        private const int MAX_CONCURRENT_ENRICHMENTS = 3;

        private YouTubeMusicClient? _ytClient;
        private SessionTokens? _sessionTokens;
        private readonly Logger _logger;
        private SpotifyIndexerSettings? _currentSettings;

        public SpotifyToYouTubeEnricher(Logger logger)
        {
            _logger = logger;
        }

        public void UpdateSettings(SpotifyIndexerSettings settings)
        {
            if (SettingsEqual(_currentSettings, settings) && _sessionTokens?.IsValid == true)
                return;

            _currentSettings = settings;

            try
            {
                _sessionTokens = TrustedSessionHelper.GetTrustedSessionTokensAsync(settings.TrustedSessionGeneratorUrl).Result;
                _ytClient = TrustedSessionHelper.CreateAuthenticatedClientAsync(settings.TrustedSessionGeneratorUrl, settings.CookiePath).Result;
                _logger.Debug("Successfully created authenticated YouTube Music client for enrichment");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to create authenticated YouTube Music client");
                _ytClient = null;
            }
        }

        public List<AlbumData> EnrichWithYouTubeData(List<AlbumData> albums)
        {
            if (_ytClient == null)
                throw new NullReferenceException("YouTube client is not initialized.");

            List<AlbumData> enrichedAlbums = new();

            foreach (AlbumData[]? batch in albums.Chunk(MAX_CONCURRENT_ENRICHMENTS).ToList())
            {
                List<Task<AlbumData>> enrichmentTasks = batch.Select(album => EnrichSingleAlbumAsync(album)).Where(x => x != null).ToList()!;

                try
                {
                    bool allCompleted = Task.WaitAll(enrichmentTasks.ToArray(), TimeSpan.FromMinutes(3));
                    if (!allCompleted)
                        _logger.Warn("Some enrichment tasks timed out after 3 minutes. Proceeding with completed tasks.");

                    foreach (Task<AlbumData> task in enrichmentTasks)
                    {
                        if (task.IsCompletedSuccessfully)
                            enrichedAlbums.Add(task.Result);
                        else if (task.IsFaulted)
                            _logger.Warn(task.Exception, "Enrichment task failed");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Unexpected error during batch enrichment");
                }
            }

            _logger.Debug($"Enriched {enrichedAlbums.Count(a => a.Bitrate > DEFAULT_BITRATE)}/{albums.Count} albums with YouTube Music data");
            return enrichedAlbums;
        }

        public async Task<AlbumData?> EnrichSingleAlbumAsync(AlbumData albumData)
        {
            try
            {

                if (string.IsNullOrWhiteSpace(albumData.AlbumName) || string.IsNullOrWhiteSpace(albumData.ArtistName))
                {
                    _logger.Trace($"Skipping enrichment due to missing album or artist name: '{albumData.AlbumName}' by '{albumData.ArtistName}'");
                    return null;
                }

                string searchQuery = $"\"{albumData.AlbumName}\" \"{albumData.ArtistName}\"";

                PaginatedAsyncEnumerable<SearchResult>? searchResults = null;
                searchResults = _ytClient!.SearchAsync(searchQuery, SearchCategory.Albums);

                if (searchResults == null)
                {
                    _logger.Debug($"No search results object created for album: '{albumData.AlbumName}' by '{albumData.ArtistName}'");
                    return null;
                }

                int processedCount = 0;
                int maxSearchResults = _currentSettings?.MaxEnrichmentAttempts ?? 10;


                await foreach (SearchResult searchResult in searchResults)
                {
                    if (searchResult is not AlbumSearchResult ytAlbum)
                        continue;

                    if (processedCount >= maxSearchResults)
                        break;

                    try
                    {
                        if (!IsAlbumMatch(albumData, ytAlbum))
                            continue;

                        string browseId;
                        AlbumInfo albumInfo;

                        try
                        {
                            browseId = await _ytClient.GetAlbumBrowseIdAsync(ytAlbum.Id);
                            albumInfo = await _ytClient.GetAlbumInfoAsync(browseId);
                        }
                        catch (Exception ex)
                        {
                            _logger.Debug(ex, $"Failed to get album info for YouTube album: '{ytAlbum.Name}' (ID: {ytAlbum.Id})");
                            continue;
                        }

                        if (albumInfo?.Songs == null || !albumInfo.Songs.Any())
                            continue;

                        if (albumData.TotalTracks > 0 && !IsTrackCountValid(albumData.TotalTracks, albumInfo.Songs.Length))
                            continue;

                        AlbumSong? firstTrack = albumInfo.Songs.FirstOrDefault(s => !string.IsNullOrEmpty(s.Id));
                        if (firstTrack?.Id == null)
                            continue;

                        StreamingData streamingData = await _ytClient.GetStreamingDataAsync(firstTrack.Id);
                        AudioStreamInfo? bestAudioStream = streamingData.StreamInfo
                            .OfType<AudioStreamInfo>()
                            .OrderByDescending(info => info.Bitrate)
                            .FirstOrDefault();

                        if (bestAudioStream != null)
                        {
                            albumData.AlbumId = ytAlbum.Id;
                            albumData.Duration = (long)albumInfo.Duration.TotalSeconds;
                            albumData.Bitrate = AudioFormatHelper.RoundToStandardBitrate(bestAudioStream.Bitrate / 1000);
                            albumData.TotalTracks = albumInfo.SongCount;
                            _logger.Trace($"Successfully enriched album: '{albumData.AlbumName}' (Bitrate: {albumData.Bitrate}kbps)");
                            return albumData;
                        }
                    }
                    catch (InvalidOperationException ex)
                    {
                        _logger.Debug(ex, $"Failed to get streaming data for a track in album '{albumData.AlbumName}'");
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, $"Failed to process YouTube album result: '{ytAlbum?.Name ?? "Unknown"}'");
                    }
                    finally
                    {
                        processedCount++;
                    }
                }
            }
            catch (IndexOutOfRangeException)
            {
                _logger.Warn($"Search pagination failed for '{albumData.AlbumName}' by '{albumData.ArtistName}'.");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Unexpected error enriching album: '{albumData.AlbumName}' by '{albumData.ArtistName}'.");
            }
            return albumData;
        }

        private bool IsAlbumMatch(AlbumData spotifyAlbum, AlbumSearchResult ytAlbum)
        {
            if (string.IsNullOrEmpty(ytAlbum.Name) || string.IsNullOrEmpty(spotifyAlbum.AlbumName))
                return false;

            string normalizedSpotifyName = NormalizeString(spotifyAlbum.AlbumName);
            string normalizedYtName = NormalizeString(ytAlbum.Name);

            bool enableFuzzyMatching = _currentSettings?.EnableFuzzyMatching ?? true;
            if (!AreNamesSimilar(normalizedSpotifyName, normalizedYtName, enableFuzzyMatching))
                return false;

            if (ytAlbum.Artists?.Any() == true && !string.IsNullOrEmpty(spotifyAlbum.ArtistName))
            {
                string normalizedSpotifyArtist = NormalizeString(spotifyAlbum.ArtistName);
                bool artistMatch = ytAlbum.Artists.Any(artist =>
                    AreNamesSimilar(normalizedSpotifyArtist, NormalizeString(artist.Name ?? ""), enableFuzzyMatching));

                if (!artistMatch)
                    return false;
            }

            if (ytAlbum.ReleaseYear > 0 && spotifyAlbum.ReleaseDateTime != DateTime.MinValue)
            {
                int yearTolerance = _currentSettings?.YearTolerance ?? 2;
                int yearDifference = Math.Abs(ytAlbum.ReleaseYear - spotifyAlbum.ReleaseDateTime.Year);
                if (yearDifference > yearTolerance)
                    return false;
            }

            return true;
        }

        private bool IsTrackCountValid(int spotifyTrackCount, int ytTrackCount)
        {
            if (spotifyTrackCount <= 0 || ytTrackCount <= 0)
                return true;
            double trackCountTolerance = (_currentSettings?.TrackCountTolerance ?? 20) / 100.0;
            double difference = Math.Abs(spotifyTrackCount - ytTrackCount) / (double)spotifyTrackCount;
            return difference <= trackCountTolerance;
        }

        private static string NormalizeString(string input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;

            return input.ToLowerInvariant()
                       .Replace("&", "and")
                       .Replace("-", " ")
                       .Replace("_", " ")
                       .Trim();
        }

        private static bool AreNamesSimilar(string name1, string name2, bool enableFuzzyMatching)
        {
            if (string.IsNullOrEmpty(name1) || string.IsNullOrEmpty(name2))
                return false;

            if (!enableFuzzyMatching)
                return string.Equals(name1, name2, StringComparison.OrdinalIgnoreCase);
            int ratio = Fuzz.Ratio(name1, name2);
            int partialRatio = Fuzz.PartialRatio(name1, name2);
            int tokenRatio = Fuzz.TokenSetRatio(name1, name2);
            return ratio >= 80 || partialRatio >= 85 || tokenRatio >= 85;
        }

        private static bool SettingsEqual(SpotifyIndexerSettings? settings1, SpotifyIndexerSettings? settings2)
        {
            if (settings1 == null || settings2 == null)
                return false;

            return settings1.CookiePath == settings2.CookiePath &&
                   settings1.TrustedSessionGeneratorUrl == settings2.TrustedSessionGeneratorUrl;
        }
    }
}
