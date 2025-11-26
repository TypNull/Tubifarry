using Tubifarry.Download.Clients.YouTube;

namespace Tubifarry.Core.Utilities
{
    public enum AudioFormat
    {
        Unknown,
        AAC,
        MP3,
        Opus,
        Vorbis,
        FLAC,
        WAV,
        MP4,
        AIFF,
        OGG,
        MIDI,
        AMR,
        WMA,
        ALAC,
        APE,
        AC3,
        EAC3
    }

    internal static class AudioFormatHelper
    {
        private static readonly AudioFormat[] _lossyFormats = [
            AudioFormat.AAC,
            AudioFormat.MP3,
            AudioFormat.Opus,
            AudioFormat.Vorbis,
            AudioFormat.MP4,
            AudioFormat.AMR,
            AudioFormat.WMA,
            AudioFormat.AC3,
            AudioFormat.EAC3
        ];

        private static readonly int[] _standardBitrates = [
            0,      // VBR (Variable Bitrate)
            32,     // Low-quality voice
            64,     // Basic music/voice
            96,     // FM radio quality
            128,    // Standard streaming (e.g., Spotify Free)
            160,    // Mid-tier music
            192,    // High-quality streaming (e.g., YouTube)
            256,    // Premium streaming (e.g., Spotify Premium)
            320,    // MP3/AAC upper limit (near-CD quality)
            384,    // Dolby Digital/AC-3 (5.1 surround)
            448,    // Dolby Digital/AC-3 (higher-end)
            510     // Opus maximum
        ];

        /// <summary>
        /// Defines bitrate constraints for lossy audio formats: (Default, Minimum, Maximum)
        /// </summary>
        private static readonly Dictionary<AudioFormat, (int Default, int Min, int Max)> FormatBitrates = new()
        {
            { AudioFormat.AAC,    (256, 64, 320) },
            { AudioFormat.MP3,    (320, 64, 320) },
            { AudioFormat.Opus,   (256, 32, 510) },
            { AudioFormat.Vorbis, (224, 64, 500) },
            { AudioFormat.MP4,    (256, 64, 320) },
            { AudioFormat.AMR,    (12, 5, 12) },
            { AudioFormat.WMA,    (192, 48, 320) },
            { AudioFormat.OGG,    (224, 64, 500) },
            { AudioFormat.AC3,    (448, 192, 640) },
            { AudioFormat.EAC3,   (768, 192, 6144) }
        };

        /// <summary>
        /// Maps approximate bitrates to Vorbis quality levels (-q:a)
        /// </summary>
        private static readonly Dictionary<int, int> VorbisBitrateToQuality = new()
        {
            { 64, 0 },    // q0 ~64kbps
            { 80, 1 },    // q1 ~80kbps
            { 96, 2 },    // q2 ~96kbps
            { 112, 3 },   // q3 ~112kbps
            { 128, 4 },   // q4 ~128kbps
            { 160, 5 },   // q5 ~160kbps
            { 192, 6 },   // q6 ~192kbps
            { 224, 7 },   // q7 ~224kbps
            { 256, 8 },   // q8 ~256kbps
            { 320, 9 },   // q9 ~320kbps
            { 500, 10 }   // q10 ~500kbps
        };

        /// <summary>
        /// Returns the correct file extension for a given audio codec.
        /// </summary>
        public static string GetFileExtensionForCodec(string codec) => codec switch
        {
            "aac" => ".m4a",
            "mp3" => ".mp3",
            "opus" => ".opus",
            "flac" => ".flac",
            "ac3" => ".ac3",
            "eac3" or "ec3" => ".ec3",
            "alac" => ".m4a",
            "vorbis" => ".ogg",
            "ape" => ".ape",
            "pcm_s16le" or "pcm_s24le" or "pcm_s32le" => ".wav",
            _ => ".aac" // Default to AAC if the codec is unknown
        };

        /// <summary>
        /// Determines the audio format from a given codec string.
        /// </summary>
        public static AudioFormat GetAudioFormatFromCodec(string codec) => codec?.ToLowerInvariant() switch
        {
            // Common codecs and extensions
            "aac" or "m4a" or "mp4" => AudioFormat.AAC,
            "mp3" => AudioFormat.MP3,
            "opus" => AudioFormat.Opus,
            "vorbis" or "ogg" => AudioFormat.Vorbis,
            "flac" => AudioFormat.FLAC,
            "wav" or "pcm_s16le" or "pcm_s24le" or "pcm_s32le" or "pcm_f32le" => AudioFormat.WAV,
            "aiff" or "aif" or "aifc" => AudioFormat.AIFF,
            "mid" or "midi" => AudioFormat.MIDI,
            "amr" => AudioFormat.AMR,
            "wma" => AudioFormat.WMA,
            "alac" => AudioFormat.ALAC,
            "ape" => AudioFormat.APE,
            "ac3" or "ac-3" => AudioFormat.AC3,
            "eac3" or "eac-3" or "e-ac-3" or "ec-3" or "ec3" => AudioFormat.EAC3,
            _ => AudioFormat.Unknown // Default for unknown formats
        };

        /// <summary>
        /// Returns the file extension for a given audio format.
        /// </summary>
        public static string GetFileExtensionForFormat(AudioFormat format) => format switch
        {
            AudioFormat.AAC => ".m4a",
            AudioFormat.MP3 => ".mp3",
            AudioFormat.Opus => ".opus",
            AudioFormat.Vorbis => ".ogg",
            AudioFormat.FLAC => ".flac",
            AudioFormat.WAV => ".wav",
            AudioFormat.AIFF => ".aiff",
            AudioFormat.MIDI => ".midi",
            AudioFormat.AMR => ".amr",
            AudioFormat.WMA => ".wma",
            AudioFormat.MP4 => ".mp4",
            AudioFormat.OGG => ".ogg",
            AudioFormat.ALAC => ".m4a",
            AudioFormat.APE => ".ape",
            AudioFormat.AC3 => ".ac3",
            AudioFormat.EAC3 => ".ec3",
            _ => ".aac" // Default to AAC if the format is unknown
        };

        /// <summary>
        /// Converts a <see cref="ReEncodeOptions"/> value to the corresponding <see cref="AudioFormat"/>.
        /// </summary>
        public static AudioFormat ConvertOptionToAudioFormat(ReEncodeOptions reEncodeOption) => reEncodeOption switch
        {
            ReEncodeOptions.AAC => AudioFormat.AAC,
            ReEncodeOptions.MP3 => AudioFormat.MP3,
            ReEncodeOptions.Opus => AudioFormat.Opus,
            ReEncodeOptions.Vorbis => AudioFormat.Vorbis,
            _ => AudioFormat.Unknown
        };

        /// <summary>
        /// Determines if a given format is lossy.
        /// </summary>
        public static bool IsLossyFormat(AudioFormat format) => _lossyFormats.Contains(format);

        /// <summary>
        /// Determines the audio format from a given file extension.
        /// </summary>
        public static AudioFormat GetAudioCodecFromExtension(string extension) => extension?.ToLowerInvariant().TrimStart('.') switch
        {
            // Common file extensions
            "m4a" or "mp4" or "aac" => AudioFormat.AAC,
            "mp3" => AudioFormat.MP3,
            "opus" => AudioFormat.Opus,
            "ogg" or "vorbis" => AudioFormat.Vorbis,
            "flac" => AudioFormat.FLAC,
            "wav" => AudioFormat.WAV,
            "aiff" or "aif" or "aifc" => AudioFormat.AIFF,
            "mid" or "midi" => AudioFormat.MIDI,
            "amr" => AudioFormat.AMR,
            "wma" => AudioFormat.WMA,
            "alac" => AudioFormat.ALAC,
            "ape" => AudioFormat.APE,
            "ac3" => AudioFormat.AC3,
            "ec3" or "eac3" => AudioFormat.EAC3,
            _ => AudioFormat.Unknown
        };

        /// <summary>
        /// Returns the default bitrate for a given audio format.
        /// </summary>
        public static int GetDefaultBitrate(AudioFormat format) =>
            FormatBitrates.TryGetValue(format, out (int Default, int Min, int Max) rates) ? rates.Default : 256;

        /// <summary>
        /// Clamps a requested bitrate to the valid range for a given format.
        /// </summary>
        public static int ClampBitrate(AudioFormat format, int requestedBitrate)
            => !FormatBitrates.TryGetValue(format, out (int Default, int Min, int Max) rates) ? requestedBitrate : Math.Clamp(requestedBitrate, rates.Min, rates.Max);

        /// <summary>
        /// Maps a target bitrate to the appropriate Vorbis quality level.
        /// </summary>
        public static int MapBitrateToVorbisQuality(int targetBitrate) => VorbisBitrateToQuality
                .OrderBy(kvp => Math.Abs(kvp.Key - targetBitrate))
                .First().Value;

        /// <summary>
        /// Rounds a bitrate to the nearest standard value.
        /// </summary>
        public static int RoundToStandardBitrate(int bitrateKbps) => _standardBitrates.OrderBy(b => Math.Abs(b - bitrateKbps)).First();
    }
}