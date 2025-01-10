using NzbDrone.Core.Download.Clients.YouTube;

namespace Tubifarry.Core
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
        WMA
    }

    internal static class AudioFormatHelper
    {
        private static readonly AudioFormat[] _lossyFormats = new[] {
            AudioFormat.AAC,
            AudioFormat.MP3,
            AudioFormat.Opus,
            AudioFormat.Vorbis,
            AudioFormat.MP4,
            AudioFormat.AMR,
            AudioFormat.WMA
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
            "alac" => ".m4a",
            "vorbis" => ".ogg",
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
            "wav" or "pcm_s16le" or "pcm_s24le" or "pcm_s32le" => AudioFormat.WAV,
            "aiff" or "aif" or "aifc" => AudioFormat.AIFF,
            "mid" or "midi" => AudioFormat.MIDI,
            "amr" => AudioFormat.AMR,
            "wma" => AudioFormat.WMA,
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
            "flac" or "alac" => AudioFormat.FLAC,
            "wav" => AudioFormat.WAV,
            "aiff" or "aif" or "aifc" => AudioFormat.AIFF,
            "mid" or "midi" => AudioFormat.MIDI,
            "amr" => AudioFormat.AMR,
            "wma" => AudioFormat.WMA,
            _ => AudioFormat.Unknown // Default for unknown extensions
        };
    }
}