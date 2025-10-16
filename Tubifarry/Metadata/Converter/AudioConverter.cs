using NLog;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using NzbDrone.Core.Tags;
using Tubifarry.Core.Model;
using Tubifarry.Core.Utilities;
using Xabe.FFmpeg;

namespace Tubifarry.Metadata.Converter
{
    public class AudioConverter(Logger logger, Lazy<ITagService> tagService) : MetadataBase<AudioConverterSettings>
    {
        private readonly Logger _logger = logger;
        private readonly Lazy<ITagService> _tagService = tagService;

        public override string Name => "Codec Tinker";

        public override MetadataFile FindMetadataFile(Artist artist, string path) => default!;
        public override MetadataFileResult ArtistMetadata(Artist artist) => default!;
        public override MetadataFileResult AlbumMetadata(Artist artist, Album album, string albumPath) => default!;
        public override List<ImageFileResult> ArtistImages(Artist artist) => default!;
        public override List<ImageFileResult> AlbumImages(Artist artist, Album album, string albumFolder) => default!;
        public override List<ImageFileResult> TrackImages(Artist artist, TrackFile trackFile) => default!;

        public override MetadataFileResult TrackMetadata(Artist artist, TrackFile trackFile)
        {
            if (ShouldConvertTrack(trackFile).Result)
                ConvertTrack(trackFile).Wait();
            else
                _logger.Trace($"No rule matched for {trackFile.OriginalFilePath}");
            return null!;
        }

        private async Task ConvertTrack(TrackFile trackFile)
        {
            AudioFormat trackFormat = await GetTrackAudioFormatAsync(trackFile.Path);
            if (trackFormat == AudioFormat.Unknown)
                return;

            int? currentBitrate = await GetTrackBitrateAsync(trackFile.Path);

            (AudioFormat targetFormat, int? targetBitrate) = GetTargetConversionForTrack(trackFormat, currentBitrate, trackFile);
            if (targetFormat == AudioFormat.Unknown)
                return;

            LogConversionPlan(trackFormat, currentBitrate, targetFormat, targetBitrate, trackFile.Path);

            await PerformConversion(trackFile, targetFormat, targetBitrate);
        }

        private async Task PerformConversion(TrackFile trackFile, AudioFormat targetFormat, int? targetBitrate)
        {
            AudioMetadataHandler audioHandler = new(trackFile.Path);
            bool success = await audioHandler.TryConvertToFormatAsync(targetFormat, targetBitrate);
            trackFile.Path = audioHandler.TrackPath;

            if (success)
                _logger.Info($"Successfully converted track: {trackFile.Path}");
            else
                _logger.Warn($"Failed to convert track: {trackFile.Path}");
        }

        private async Task<int?> GetTrackBitrateAsync(string filePath)
        {
            try
            {
                IMediaInfo mediaInfo = await FFmpeg.GetMediaInfo(filePath);
                IAudioStream? audioStream = mediaInfo.AudioStreams.FirstOrDefault();

                if (audioStream == null)
                    return null;

                return AudioFormatHelper.RoundToStandardBitrate((int)(audioStream.Bitrate / 1000));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to get bitrate: {0}", filePath);
                return null;
            }
        }

        private (AudioFormat TargetFormat, int? TargetBitrate) GetTargetConversionForTrack(AudioFormat trackFormat, int? currentBitrate, TrackFile trackFile)
        {
            ConversionRule? artistRule = GetArtistTagRule(trackFile);
            if (artistRule != null)
            {
                if (artistRule.TargetFormat == AudioFormat.Unknown)
                    return (AudioFormat.Unknown, null);

                if (AudioFormatHelper.IsLossyFormat(trackFormat) && !AudioFormatHelper.IsLossyFormat(artistRule.TargetFormat))
                {
                    _logger.Warn($"Blocked lossy to lossless conversion from {trackFormat} to {artistRule.TargetFormat}");
                    return (AudioFormat.Unknown, null);
                }

                _logger.Debug($"Using artist tag rule for {trackFile.Artist?.Value?.Name}: {artistRule.TargetFormat}" +
                             (artistRule.TargetBitrate.HasValue ? $":{artistRule.TargetBitrate}kbps" : ""));
                return (artistRule.TargetFormat, artistRule.TargetBitrate);
            }

            foreach (KeyValuePair<string, string> ruleEntry in Settings.CustomConversion)
            {
                if (!RuleParser.TryParseRule(ruleEntry.Key, ruleEntry.Value, out ConversionRule rule))
                    continue;

                if (!IsRuleMatching(rule, trackFormat, currentBitrate))
                    continue;

                if (AudioFormatHelper.IsLossyFormat(trackFormat) && !AudioFormatHelper.IsLossyFormat(rule.TargetFormat))
                {
                    _logger.Warn($"Blocked lossy to lossless conversion from {trackFormat}");
                    return (AudioFormat.Unknown, null);
                }

                return (rule.TargetFormat, rule.TargetBitrate);
            }
            return ((AudioFormat)Settings.TargetFormat, null);
        }

        private async Task<bool> ShouldConvertTrack(TrackFile trackFile)
        {
            ConversionRule? artistRule = GetArtistTagRule(trackFile);
            if (artistRule != null && artistRule.TargetFormat == AudioFormat.Unknown)
            {
                _logger.Debug($"Skipping conversion due to no-conversion artist tag for {trackFile.Artist?.Value?.Name}");
                return false;
            }

            AudioFormat trackFormat = await GetTrackAudioFormatAsync(trackFile.Path);
            if (trackFormat == AudioFormat.Unknown)
                return false;

            int? currentBitrate = await GetTrackBitrateAsync(trackFile.Path);
            _logger.Trace($"Track bitrate found for {trackFile.Path} at {currentBitrate ?? 0}kbps");

            if (artistRule != null)
                return true;
            if (MatchesAnyCustomRule(trackFormat, currentBitrate))
                return true;
            return IsFormatEnabledForConversion(trackFormat);
        }

        private ConversionRule? GetArtistTagRule(TrackFile trackFile)
        {
            if (trackFile.Artist?.Value?.Tags == null || trackFile.Artist.Value.Tags.Count == 0)
                return null;

            foreach (Tag? tag in trackFile.Artist.Value.Tags.Select(x => _tagService.Value.GetTag(x)))
            {
                if (RuleParser.TryParseArtistTag(tag.Label, out ConversionRule rule))
                {
                    _logger.Debug($"Found artist tag rule: {tag.Label} for {trackFile.Artist.Value.Name}");
                    return rule;
                }
            }
            return null;
        }

        private bool MatchesAnyCustomRule(AudioFormat trackFormat, int? currentBitrate) =>
            Settings.CustomConversion.Any(ruleEntry => RuleParser.TryParseRule(ruleEntry.Key, ruleEntry.Value, out ConversionRule rule) && IsRuleMatching(rule, trackFormat, currentBitrate));

        private bool IsRuleMatching(ConversionRule rule, AudioFormat trackFormat, int? currentBitrate)
        {
            bool formatMatches = rule.MatchesFormat(trackFormat);
            bool bitrateMatches = rule.MatchesBitrate(currentBitrate);
            if (formatMatches && bitrateMatches)
            {
                _logger.Debug($"Matched conversion rule: {rule}");
                return true;
            }
            return false;
        }

        private async Task<AudioFormat> GetTrackAudioFormatAsync(string trackPath)
        {
            string extension = Path.GetExtension(trackPath);

            // For .m4a files, use codec detection since they can contain AAC or ALAC
            if (string.Equals(extension, ".m4a", StringComparison.OrdinalIgnoreCase))
            {
                AudioFormat detectedFormat = await AudioMetadataHandler.GetSupportedCodecAsync(trackPath);
                if (detectedFormat != AudioFormat.Unknown)
                {
                    _logger.Trace($"Detected codec-based format {detectedFormat} for .m4a file: {trackPath}");
                    return detectedFormat;
                }

                _logger.Warn($"Failed to detect codec for .m4a file, falling back to extension-based detection: {trackPath}");
            }

            // For all other extensions, use extension-based detection
            AudioFormat trackFormat = AudioFormatHelper.GetAudioCodecFromExtension(extension);
            if (trackFormat == AudioFormat.Unknown)
                _logger.Warn($"Unknown audio format for track: {trackPath}");
            return trackFormat;
        }

        private void LogConversionPlan(AudioFormat sourceFormat, int? sourceBitrate, AudioFormat targetFormat, int? targetBitrate, string trackPath)
        {
            string sourceDescription = FormatDescriptionWithBitrate(sourceFormat, sourceBitrate);
            string targetDescription = FormatDescriptionWithBitrate(targetFormat, targetBitrate);

            _logger.Debug($"Converting {sourceDescription} to {targetDescription}: {trackPath}");
        }

        private static string FormatDescriptionWithBitrate(AudioFormat format, int? bitrate)
            => format + (bitrate.HasValue ? $" ({bitrate}kbps)" : "");

        private bool IsFormatEnabledForConversion(AudioFormat format) => format switch
        {
            AudioFormat.MP3 => Settings.ConvertMP3,
            AudioFormat.AAC => Settings.ConvertAAC,
            AudioFormat.FLAC => Settings.ConvertFLAC,
            AudioFormat.WAV => Settings.ConvertWAV,
            AudioFormat.Opus => Settings.ConvertOpus,
            AudioFormat.APE => Settings.ConvertOther,
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