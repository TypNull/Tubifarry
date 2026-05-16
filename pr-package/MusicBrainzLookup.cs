using System.Text.Json;

namespace Tubifarry.Core.Records
{
    /// <summary>
    /// MusicBrainz IDs for album, release, artist, and tracks.
    /// </summary>
    public record MusicBrainzIds
    {
        public string? ReleaseId { get; init; }
        public string? ReleaseGroupId { get; init; }
        public string? ArtistId { get; init; }
        public string? ReleaseArtistId { get; init; }
        public Dictionary<int, string>? TrackRecordingIds { get; init; }
    }

    /// <summary>
    /// MusicBrainz release information from API lookup.
    /// </summary>
    public record MusicBrainzRelease
    {
        public string ReleaseId { get; init; } = "";
        public string ReleaseGroupId { get; init; } = "";
        public string ArtistId { get; init; } = "";
        public string ArtistName { get; init; } = "";
        public string Title { get; init; } = "";
        public int TrackCount { get; init; }
        public List<MusicBrainzTrack>? Tracks { get; init; }
    }

    /// <summary>
    /// MusicBrainz track information.
    /// </summary>
    public record MusicBrainzTrack
    {
        public string RecordingId { get; init; } = "";
        public string Title { get; init; } = "";
        public int Position { get; init; }
    }

    /// <summary>
    /// Looks up MusicBrainz IDs for tracks/albums to enable
    /// seamless import into Lidarr. Uses the MusicBrainz web service API.
    /// </summary>
    public class MusicBrainzLookup
    {
        private readonly HttpClient _client;
        private readonly string _userAgent;

        public MusicBrainzLookup(HttpClient? client = null, string? userAgent = null)
        {
            _client = client ?? new HttpClient();
            _userAgent = userAgent ?? "Tubifarry/1.0 (https://github.com/TypNull/Tubifarry)";
        }

        /// <summary>
        /// Looks up a MusicBrainz release by artist and album name.
        /// Returns the best matching release with MBIDs, or null if not found.
        /// </summary>
        public async Task<MusicBrainzRelease?> LookupReleaseAsync(string artist, string album, int? year = null, CancellationToken token = default)
        {
            var query = $"release:\"{EscapeSearch(album)}\" AND artist:\"{EscapeSearch(artist)}\"";
            if (year.HasValue)
            {
                query += $" AND date:{year.Value}";
            }

            var url = $"https://musicbrainz.org/ws/2/release?query={Uri.EscapeDataString(query)}&fmt=json&limit=5";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", _userAgent);

            try
            {
                var response = await _client.SendAsync(request, token);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync(token);
                using var doc = JsonDocument.Parse(json);

                var releases = doc.RootElement.GetProperty("releases");
                if (releases.GetArrayLength() == 0)
                {
                    return null;
                }

                // Score and pick the best match
                foreach (var release in releases.EnumerateArray())
                {
                    var score = release.TryGetProperty("score", out var scoreEl) ? scoreEl.GetInt32() : 0;
                    if (score < 80)
                    {
                        continue;
                    }

                    var mbRelease = ParseRelease(release);
                    if (mbRelease != null)
                    {
                        return mbRelease;
                    }
                }

                // Fallback: take the first result even with lower score
                if (releases.GetArrayLength() > 0)
                {
                    return ParseRelease(releases[0]);
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Looks up a MusicBrainz recording (track) by artist and track title.
        /// Returns the recording MBID, or null if not found.
        /// </summary>
        public async Task<string?> LookupRecordingAsync(string artist, string trackTitle, CancellationToken token = default)
        {
            var query = $"recording:\"{EscapeSearch(trackTitle)}\" AND artist:\"{EscapeSearch(artist)}\"";
            var url = $"https://musicbrainz.org/ws/2/recording?query={Uri.EscapeDataString(query)}&fmt=json&limit=1";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", _userAgent);

            try
            {
                var response = await _client.SendAsync(request, token);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync(token);
                using var doc = JsonDocument.Parse(json);

                var recordings = doc.RootElement.GetProperty("recordings");
                if (recordings.GetArrayLength() == 0)
                {
                    return null;
                }

                return recordings[0].GetProperty("id").GetString();
            }
            catch
            {
                return null;
            }
        }

        private static MusicBrainzRelease? ParseRelease(JsonElement release)
        {
            try
            {
                var mbRelease = new MusicBrainzRelease
                {
                    ReleaseId = release.GetProperty("id").GetString() ?? "",
                    Title = release.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                };

                // Release group
                if (release.TryGetProperty("release-group", out var rg))
                {
                    mbRelease.ReleaseGroupId = rg.TryGetProperty("id", out var rgId) ? rgId.GetString() ?? "" : "";
                }

                // Artist credit -> first artist
                if (release.TryGetProperty("artist-credit", out var ac) && ac.GetArrayLength() > 0)
                {
                    var firstArtist = ac[0].GetProperty("artist");
                    mbRelease.ArtistId = firstArtist.TryGetProperty("id", out var artId) ? artId.GetString() ?? "" : "";
                    mbRelease.ArtistName = firstArtist.TryGetProperty("name", out var artName) ? artName.GetString() ?? "" : "";
                }

                // Media -> tracks
                if (release.TryGetProperty("media", out var media) && media.GetArrayLength() > 0)
                {
                    var firstMedia = media[0];
                    if (firstMedia.TryGetProperty("track", out var tracks))
                    {
                        mbRelease.TrackCount = tracks.GetArrayLength();
                        mbRelease.Tracks = new List<MusicBrainzTrack>();
                        foreach (var track in tracks.EnumerateArray())
                        {
                            mbRelease.Tracks.Add(new MusicBrainzTrack
                            {
                                RecordingId = track.TryGetProperty("recording", out var rec)
                                    ? rec.TryGetProperty("id", out var recId) ? recId.GetString() ?? "" : ""
                                    : "",
                                Title = track.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                                Position = track.TryGetProperty("position", out var pos) ? pos.GetInt32() : 0,
                            });
                        }
                    }
                }

                return mbRelease;
            }
            catch
            {
                return null;
            }
        }

        private static string EscapeSearch(string input)
        {
            return input.Replace("\"", "\\\"");
        }
    }
}