using NLog;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using Tubifarry.Core.Model;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Metadata.Converter
{
    public class AudioConverter : MetadataBase<AudioConverterSettings>
    {
        private readonly Logger _logger;

        public AudioConverter(Logger logger) => _logger = logger;

        public override string Name => "Codec Tinker";

        public override MetadataFile FindMetadataFile(Artist artist, string path) => default!;

        public override MetadataFileResult ArtistMetadata(Artist artist) => default!;

        public override MetadataFileResult AlbumMetadata(Artist artist, Album album, string albumPath) => default!;

        public override MetadataFileResult TrackMetadata(Artist artist, TrackFile trackFile)
        {
            if (ShouldConvertTrack(trackFile))
                ConvertTrack(trackFile).Wait();
            return null!;
        }

        public override List<ImageFileResult> ArtistImages(Artist artist) => default!;

        public override List<ImageFileResult> AlbumImages(Artist artist, Album album, string albumFolder) => default!;

        public override List<ImageFileResult> TrackImages(Artist artist, TrackFile trackFile) => default!;

        private async Task ConvertTrack(TrackFile trackFile)
        {
            AudioFormat trackFormat = AudioFormatHelper.GetAudioCodecFromExtension(Path.GetExtension(trackFile.Path));
            if (trackFormat == AudioFormat.Unknown)
            {
                _logger.Warn("Unknown audio format for track: {0}", trackFile.Path);
                return;
            }

            AudioFormat targetFormat = GetTargetFormatForTrack(trackFormat);
            if (targetFormat == AudioFormat.Unknown)
                return;

            _logger.Debug("Converting track from {0} to {1}: {2}", trackFormat, targetFormat, trackFile.Path);

            AudioMetadataHandler audioHandler = new(trackFile.Path);
            bool success = await audioHandler.TryConvertToFormatAsync(targetFormat);
            trackFile.Path = audioHandler.TrackPath;

            if (success)
                _logger.Info("Successfully converted track: {0}", trackFile.Path);
            else
                _logger.Warn("Failed to convert track: {0}", trackFile.Path);
        }

        private AudioFormat GetTargetFormatForTrack(AudioFormat trackFormat)
        {
            foreach (KeyValuePair<string, string> rule in Settings.CustomConversion)
            {
                if (rule.Key.Equals(trackFormat.ToString(), StringComparison.OrdinalIgnoreCase) && Enum.TryParse(rule.Value, true, out AudioFormat customTargetFormat))
                {
                    _logger.Trace("Using custom target format {0} for track format {1}", customTargetFormat, trackFormat);
                    return customTargetFormat;
                }
            }
            if (Settings.CustomConversion.FirstOrDefault(x => x.Key.Equals("all", StringComparison.OrdinalIgnoreCase)) is KeyValuePair<string, string> all && Enum.TryParse(all.Value, true, out AudioFormat customAllTargetsFormat))
            {
                _logger.Trace("Using custom target format {0} for track format {1}", customAllTargetsFormat, trackFormat);
                if (AudioFormatHelper.IsLossyFormat(trackFormat) && !AudioFormatHelper.IsLossyFormat(customAllTargetsFormat))
                {
                    _logger.Warn("Blocked lossy ➔ lossless conversion via 'all' rule for: {0}", trackFormat);
                    return AudioFormat.Unknown;
                }
                return customAllTargetsFormat;
            }
            return (AudioFormat)Settings.TargetFormat;
        }

        private bool ShouldConvertTrack(TrackFile trackFile)
        {
            AudioFormat trackFormat = AudioFormatHelper.GetAudioCodecFromExtension(Path.GetExtension(trackFile.Path));
            if (trackFormat == AudioFormat.Unknown)
            {
                _logger.Warn("Unknown audio format for track: {0}", trackFile.Path);
                return false;
            }

            bool hasDirectRule = Settings.CustomConversion.Any(r => r.Key.Equals(trackFormat.ToString(), StringComparison.OrdinalIgnoreCase));
            bool hasGlobalRule = Settings.CustomConversion.Any(r => r.Key.Equals("all", StringComparison.OrdinalIgnoreCase));
            bool defaultConversion = IsFormatEnabledForConversion(trackFormat);
            return hasDirectRule || hasGlobalRule || defaultConversion;
        }

        private bool IsFormatEnabledForConversion(AudioFormat format) => format switch
        {
            AudioFormat.MP3 => Settings.ConvertMP3,
            AudioFormat.AAC => Settings.ConvertAAC,
            AudioFormat.FLAC => Settings.ConvertFLAC,
            AudioFormat.WAV => Settings.ConvertWAV,
            AudioFormat.Opus => Settings.ConvertOpus,
            AudioFormat.Vorbis => Settings.ConvertOther,
            AudioFormat.OGG => Settings.ConvertOther,
            AudioFormat.WMA => Settings.ConvertOther,
            AudioFormat.ALAC => Settings.ConvertOther,
            AudioFormat.AIFF => Settings.ConvertOther,
            AudioFormat.AMR => Settings.ConvertOther,
            _ => false
        };
    }
}