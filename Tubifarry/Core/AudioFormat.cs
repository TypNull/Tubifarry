﻿using NzbDrone.Core.Download.Clients.YouTube;

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
        public static AudioFormat GetAudioFormatFromCodec(string codec) => codec switch
        {
            "aac" => AudioFormat.AAC,
            "mp3" => AudioFormat.MP3,
            "opus" => AudioFormat.Opus,
            "vorbis" => AudioFormat.Vorbis,
            "flac" => AudioFormat.FLAC,
            "pcm_s16le" or "pcm_s24le" or "pcm_s32le" => AudioFormat.WAV,
            _ => AudioFormat.Unknown
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
            _ => ".aac"
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
    }
}