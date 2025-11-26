using NzbDrone.Core.Parser.Model;
using System.Text.Json;
using Tubifarry.Core.Model;

namespace Tubifarry.Core.Utilities
{
    /// <summary>
    /// Common helper utilities for indexer parsers to reduce code duplication
    /// </summary>
    public static class IndexerParserHelper
    {
        /// <summary>
        /// Standard JSON serialization options used across all indexers
        /// </summary>
        public static readonly JsonSerializerOptions StandardJsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
        };

        /// <summary>
        /// Processes a collection of items and converts them to ReleaseInfo
        /// Common pattern used across multiple indexers
        /// </summary>
        /// <typeparam name="T">Type of items to process</typeparam>
        /// <param name="items">Collection of items to process</param>
        /// <param name="createData">Function to create AlbumData from item</param>
        /// <param name="releases">List to add releases to</param>
        public static void ProcessItems<T>(
            IList<T>? items,
            Func<T, AlbumData> createData,
            List<ReleaseInfo> releases)
        {
            if ((items?.Count ?? 0) <= 0)
                return;

            foreach (T item in items!)
            {
                AlbumData data = createData(item);
                data.ParseReleaseDate();
                releases.Add(data.ToReleaseInfo());
            }
        }

        /// <summary>
        /// Determines audio format from file extension or content type/MIME type
        /// </summary>
        /// <param name="fileExtension">File extension (e.g., "flac", "mp3")</param>
        /// <param name="contentType">MIME type or codec (e.g., "audio/flac")</param>
        /// <param name="defaultFormat">Default format if detection fails</param>
        /// <returns>Detected AudioFormat</returns>
        public static AudioFormat DetermineFormat(
            string? fileExtension,
            string? contentType,
            AudioFormat defaultFormat = AudioFormat.MP3)
        {
            if (!string.IsNullOrEmpty(fileExtension))
            {
                var format = AudioFormatHelper.GetAudioCodecFromExtension(fileExtension);
                if (format != AudioFormat.Unknown)
                    return format;
            }

            if (!string.IsNullOrEmpty(contentType))
            {
                string codec = contentType.Contains('/')
                    ? contentType.Split('/').Last()
                    : contentType;

                var format = AudioFormatHelper.GetAudioFormatFromCodec(codec);
                if (format != AudioFormat.Unknown)
                    return format;
            }

            return defaultFormat;
        }

        /// <summary>
        /// Gets or estimates the size of an audio file/album
        /// </summary>
        /// <param name="actualSize">Actual size if known</param>
        /// <param name="durationSeconds">Duration in seconds</param>
        /// <param name="bitrateKbps">Bitrate in kbps</param>
        /// <param name="trackCount">Number of tracks</param>
        /// <param name="defaultSizePerTrack">Default size per track in bytes (default 50MB)</param>
        /// <returns>Size in bytes</returns>
        public static long EstimateSize(
            long actualSize,
            long durationSeconds,
            int bitrateKbps,
            int trackCount = 1,
            long defaultSizePerTrack = 50 * 1024 * 1024)
        {
            // If actual size is known, use it
            if (actualSize > 0)
                return actualSize;
            if (durationSeconds > 0 && bitrateKbps > 0)
                return durationSeconds * bitrateKbps * 1000 / 8;
            if (trackCount > 0)
                return trackCount * defaultSizePerTrack;
            return defaultSizePerTrack;
        }

        /// <summary>
        /// Gets quality information (format, bitrate, bit depth) from song data
        /// </summary>
        /// <param name="fileExtension">File extension</param>
        /// <param name="contentType">Content/MIME type</param>
        /// <param name="reportedBitrate">Bitrate reported by source</param>
        /// <param name="defaultBitDepth">Default bit depth (16 for CD quality)</param>
        /// <returns>Tuple of (AudioFormat, Bitrate, BitDepth)</returns>
        public static (AudioFormat Format, int Bitrate, int BitDepth) GetQualityInfo(
            string? fileExtension,
            string? contentType,
            int reportedBitrate,
            int defaultBitDepth = 16)
        {
            AudioFormat format = DetermineFormat(fileExtension, contentType);
            int bitrate = reportedBitrate > 0 ? reportedBitrate : AudioFormatHelper.GetDefaultBitrate(format);
            int bitDepth = defaultBitDepth;

            // Infer higher quality from bitrate for lossless formats
            if (format == AudioFormat.FLAC)
            {
                if (bitrate >= 2304)
                    bitDepth = 24; // Hi-Res audio
                else if (bitrate >= 1411)
                    bitDepth = 16; // CD quality
            }

            return (format, bitrate, bitDepth);
        }
    }
}
