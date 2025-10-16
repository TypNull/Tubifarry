using NLog;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xabe.FFmpeg;

namespace Tubifarry.Download.Clients.YouTube
{
    public class SponsorBlock
    {
        private readonly Logger _logger = LogManager.GetCurrentClassLogger();
        private readonly string _filePath;
        private readonly string _videoId;
        private readonly string _apiEndpoint;

        public SponsorBlock(string filePath, string videoId, string apiEndpoint = "https://sponsor.ajay.app")
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path cannot be null or empty");

            if (string.IsNullOrWhiteSpace(videoId))
                throw new ArgumentException("Video ID cannot be null or empty");

            if (videoId.Length != 11)
                throw new ArgumentException("Video ID must be exactly 11 characters");

            if (string.IsNullOrWhiteSpace(apiEndpoint))
                throw new ArgumentException("API endpoint cannot be null or empty");

            _filePath = filePath;
            _videoId = videoId;
            _apiEndpoint = apiEndpoint;
        }

        public async Task<bool> LookupAndTrimAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    _logger.Error($"Audio file not found: {_filePath}");
                    return false;
                }

                List<SponsorSegment> segments = await FetchNonMusicSegmentsAsync(cancellationToken);
                if (segments.Count == 0)
                {
                    _logger.Trace($"No non-music segments found for video {_videoId}");
                    return true;
                }
                bool result = await TrimSegmentsAsync(segments, cancellationToken);

                _logger.Debug("SponsorBlock processing completed");
                return result;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to process SponsorBlock segments for {_videoId}");
                return false;
            }
        }

        private async Task<List<SponsorSegment>> FetchNonMusicSegmentsAsync(CancellationToken cancellationToken)
        {
            string url = $"{_apiEndpoint.TrimEnd('/')}/api/skipSegments?videoID={_videoId}&category=music_offtopic&actionType=skip";

            try
            {
                using HttpClient client = new();
                HttpResponseMessage response = await client.GetAsync(url, cancellationToken);

                if (response.StatusCode == HttpStatusCode.NotFound)
                    return [];

                if (!response.IsSuccessStatusCode)
                {
                    _logger.Warn($"SponsorBlock API returned {response.StatusCode} for video {_videoId}");
                    return [];
                }

                string jsonContent = await response.Content.ReadAsStringAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(jsonContent))
                    return [];

                List<SponsorSegment>? segments = JsonSerializer.Deserialize<List<SponsorSegment>>(jsonContent);
                if (segments == null)
                    return [];

                return segments.Where(s => s.Segment?.Length == 2 && s.Segment[0] < s.Segment[1]).ToList();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to fetch SponsorBlock data for video {_videoId}");
                return [];
            }
        }

        private async Task<bool> TrimSegmentsAsync(List<SponsorSegment> segments, CancellationToken cancellationToken)
        {
            string inputDir = Path.GetDirectoryName(_filePath) ?? "";
            string trackName = Path.GetFileNameWithoutExtension(_filePath);
            string tempDir = Path.Combine(inputDir, $"sponsorblock_temp_{(trackName.Length > 10 ? trackName[^10..] : trackName)}");
            Directory.CreateDirectory(tempDir);

            try
            {
                List<string> segmentFiles = [];
                List<SponsorSegment> sortedSegments = [.. segments.OrderBy(s => s.Segment![0])];
                double previousEnd = 0.0;
                int segmentIndex = 0;

                // Get total duration
                double totalDuration = sortedSegments.Max(s => s.VideoDuration > 0 ? s.VideoDuration : s.Segment![1] + 30);

                // Extract segments
                foreach (SponsorSegment? segment in sortedSegments)
                {
                    double segmentStart = segment.Segment![0];
                    double segmentEnd = segment.Segment[1];

                    if (segmentStart > previousEnd)
                    {
                        string segmentFile = Path.Combine(tempDir, $"segment_{segmentIndex:D3}{Path.GetExtension(_filePath)}");

                        IConversion extraction = FFmpeg.Conversions.New()
                            .AddParameter($"-i \"{_filePath}\"")
                            .AddParameter($"-ss {previousEnd.ToString("F3", CultureInfo.InvariantCulture)}")
                            .AddParameter($"-to {segmentStart.ToString("F3", CultureInfo.InvariantCulture)}")
                            .AddParameter("-c copy")
                            .AddParameter("-map 0")  // Copy all streams
                            .AddParameter("-avoid_negative_ts make_zero")
                            .SetOverwriteOutput(true)
                            .SetOutput(segmentFile);

                        await extraction.Start(cancellationToken);
                        segmentFiles.Add(segmentFile);
                        segmentIndex++;
                    }
                    previousEnd = segmentEnd;
                }

                // Add final segment
                if (previousEnd < totalDuration - 1)
                {
                    string segmentFile = Path.Combine(tempDir, $"segment_{segmentIndex:D3}{Path.GetExtension(_filePath)}");

                    IConversion extraction = FFmpeg.Conversions.New()
                        .AddParameter($"-i \"{_filePath}\"")
                        .AddParameter($"-ss {previousEnd.ToString("F3", CultureInfo.InvariantCulture)}")
                        .AddParameter("-c copy")
                        .AddParameter("-map 0")  // Copy all streams
                        .SetOverwriteOutput(true)
                        .SetOutput(segmentFile);

                    await extraction.Start(cancellationToken);
                    segmentFiles.Add(segmentFile);
                }

                if (segmentFiles.Count == 0)
                {
                    _logger.Warn($"No segments to concatenate for video {_videoId}");
                    return false;
                }

                // Create concat list
                string concatListPath = Path.Combine(tempDir, "concat_list.txt");
                await File.WriteAllLinesAsync(concatListPath,
                    segmentFiles.Select(f => $"file '{Path.GetFileName(f)}'"),
                    cancellationToken);

                // Concatenate with proper metadata handling
                string tempOutputPath = Path.Combine(inputDir, $"sponsorblock_{Guid.NewGuid()}{Path.GetExtension(_filePath)}");

                IConversion concat = FFmpeg.Conversions.New()
                    .AddParameter("-f concat")
                    .AddParameter("-safe 0")
                    .AddParameter($"-i \"{concatListPath}\"")
                    .AddParameter($"-i \"{_filePath}\"")  // Add original file for metadata
                    .AddParameter("-c copy")
                    .AddParameter("-map 0")  // Map concat result
                    .AddParameter("-map_metadata 1")  // Take metadata from original file
                    .SetOverwriteOutput(true)
                    .SetOutput(tempOutputPath);

                await concat.Start(cancellationToken);

                if (File.Exists(tempOutputPath) && new FileInfo(tempOutputPath).Length > 0)
                {
                    File.Move(tempOutputPath, _filePath, overwrite: true);
                    _logger.Debug($"Successfully removed {sortedSegments.Count} non-music segments from {Path.GetFileName(_filePath)}");
                    return true;
                }

                _logger.Error($"Concatenation failed for video {_videoId}");
                return false;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir))
                        Directory.Delete(tempDir, recursive: true);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, $"Failed to cleanup temp directory: {tempDir}");
                }
            }
        }

        private record SponsorSegment(
            [property: JsonPropertyName("segment")] double[]? Segment,
            [property: JsonPropertyName("UUID")] string? UUID,
            [property: JsonPropertyName("category")] string? Category,
            [property: JsonPropertyName("videoDuration")] double VideoDuration,
            [property: JsonPropertyName("actionType")] string? ActionType,
            [property: JsonPropertyName("locked")] int Locked,
            [property: JsonPropertyName("votes")] int Votes,
            [property: JsonPropertyName("description")] string? Description
        );
    }
}