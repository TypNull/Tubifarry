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
            if (ShouldConvertTrack(trackFile).GetAwaiter().GetResult())
                ConvertTrack(trackFile).GetAwaiter().GetResult();
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

            ConversionResult result = await GetTargetConversionForTrack(trackFormat, currentBitrate, trackFile);
            if (result.IsBlocked)
                return;

            LogConversionPlan(trackFormat, currentBitrate, result.TargetFormat, result.TargetBitrate, trackFile.Path);

            await PerformConversion(trackFile, result);
        }

        private async Task PerformConversion(TrackFile trackFile, ConversionResult result)
        {
            AudioMetadataHandler audioHandler = new(trackFile.Path);
            bool success = await audioHandler.TryConvertToFormatAsync(result.TargetFormat, result.TargetBitrate, result.TargetBitDepth, result.UseCBR);
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

        private async Task<int?> GetTrackBitDepthAsync(string filePath)
        {
            try
            {
                string probeArgs = $"-v error -select_streams a:0 -show_entries stream=bits_per_raw_sample,sample_fmt -of default=noprint_wrappers=1 \"{filePath}\"";
                string probeOutput = await Probe.New().Start(probeArgs);

                if (string.IsNullOrWhiteSpace(probeOutput))
                    return null;

                foreach (string line in probeOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.StartsWith("bits_per_raw_sample=") && int.TryParse(line.AsSpan(20), out int bitsPerRaw) && bitsPerRaw > 0)
                        return bitsPerRaw;
                }

                foreach (string line in probeOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.StartsWith("sample_fmt="))
                    {
                        string sampleFmt = line[11..].Trim().ToLower();
                        return sampleFmt switch
                        {
                            "s16" or "s16le" or "s16be" or "s16p" => 16,
                            "s24" or "s24le" or "s24be" or "s24p" => 24,
                            "s32" or "s32le" or "s32be" or "s32p" => 32,
                            _ => null
                        };
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to get bit depth: {0}", filePath);
                return null;
            }
        }


        private ConversionResult ShouldBlockConversion(ConversionRule rule, AudioFormat trackFormat, int? currentBitrate, int? currentBitDepth)
        {
            if (rule.TargetFormat == AudioFormat.Unknown)
                return ConversionResult.Blocked();

            // Block lossy to lossless conversion
            if (AudioFormatHelper.IsLossyFormat(trackFormat) && !AudioFormatHelper.IsLossyFormat(rule.TargetFormat))
            {
                _logger.Warn($"Blocked lossy to lossless conversion from {trackFormat} to {rule.TargetFormat}");
                return ConversionResult.Blocked();
            }

            // Block bitrate upsampling for lossy formats
            if (AudioFormatHelper.IsLossyFormat(trackFormat) &&
                AudioFormatHelper.IsLossyFormat(rule.TargetFormat) &&
                currentBitrate.HasValue &&
                rule.TargetBitrate.HasValue &&
                rule.TargetBitrate.Value > currentBitrate.Value)
            {
                _logger.Warn($"Blocked bitrate upsampling from {currentBitrate}kbps to {rule.TargetBitrate}kbps for {trackFormat}");
                return ConversionResult.Blocked();
            }

            // Block bit depth upsampling for lossless formats
            if (!AudioFormatHelper.IsLossyFormat(trackFormat) &&
                !AudioFormatHelper.IsLossyFormat(rule.TargetFormat) &&
                currentBitDepth.HasValue &&
                rule.TargetBitDepth.HasValue &&
                rule.TargetBitDepth.Value > currentBitDepth.Value)
            {
                _logger.Warn($"Blocked bit depth upsampling from {currentBitDepth}-bit to {rule.TargetBitDepth}-bit for {trackFormat}");
                return ConversionResult.Blocked();
            }

            return ConversionResult.FromRule(rule);
        }

        private async Task<ConversionResult> GetTargetConversionForTrack(AudioFormat trackFormat, int? currentBitrate, TrackFile trackFile)
        {
            int? currentBitDepth = null;

            // Get current bit depth for lossless formats
            if (!AudioFormatHelper.IsLossyFormat(trackFormat))
            {
                currentBitDepth = await GetTrackBitDepthAsync(trackFile.Path);
            }

            // Check artist tag rule first
            ConversionRule? artistRule = GetArtistTagRule(trackFile);
            if (artistRule != null)
            {
                ConversionResult result = ShouldBlockConversion(artistRule, trackFormat, currentBitrate, currentBitDepth);
                if (result.IsBlocked)
                    return result;

                _logger.Debug($"Using artist tag rule for {trackFile.Artist?.Value?.Name}: {artistRule.TargetFormat}" +
                             (artistRule.TargetBitrate.HasValue ? $":{artistRule.TargetBitrate}kbps" :
                              artistRule.TargetBitDepth.HasValue ? $":{artistRule.TargetBitDepth}-bit" : "") +
                             (artistRule.UseCBR ? ":cbr" : ""));
                return result;
            }

            // Check custom conversion rules
            foreach (KeyValuePair<string, string> ruleEntry in Settings.CustomConversion)
            {
                if (!RuleParser.TryParseRule(ruleEntry.Key, ruleEntry.Value, out ConversionRule rule))
                    continue;

                if (!IsRuleMatching(rule, trackFormat, currentBitrate, currentBitDepth))
                    continue;

                ConversionResult result = ShouldBlockConversion(rule, trackFormat, currentBitrate, currentBitDepth);
                if (result.IsBlocked)
                    return result;

                return result;
            }

            return ConversionResult.Success((AudioFormat)Settings.TargetFormat);
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

            int? currentBitDepth = null;
            if (!AudioFormatHelper.IsLossyFormat(trackFormat))
                currentBitDepth = await GetTrackBitDepthAsync(trackFile.Path);

            if (artistRule != null)
                return true;
            if (MatchesAnyCustomRule(trackFormat, currentBitrate, currentBitDepth))
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

        private bool MatchesAnyCustomRule(AudioFormat trackFormat, int? currentBitrate, int? currentBitDepth) =>
            Settings.CustomConversion.Any(ruleEntry => RuleParser.TryParseRule(ruleEntry.Key, ruleEntry.Value, out ConversionRule rule) && IsRuleMatching(rule, trackFormat, currentBitrate, currentBitDepth));

        private bool IsRuleMatching(ConversionRule rule, AudioFormat trackFormat, int? currentBitrate, int? currentBitDepth)
        {
            bool formatMatches = rule.MatchesFormat(trackFormat);
            int? constraintValue = AudioFormatHelper.IsLossyFormat(trackFormat) ? currentBitrate : currentBitDepth;
            bool constraintMatches = rule.MatchesSourceConstraint(constraintValue);
            if (formatMatches && constraintMatches)
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