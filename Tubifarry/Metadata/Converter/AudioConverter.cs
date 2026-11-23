using FluentValidation.Results;
using NLog;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using NzbDrone.Core.Tags;
using Tubifarry.Core.FFmpeg;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Metadata.Converter
{
    public class AudioConverter(Logger logger, Lazy<ITagService> tagService, IAudioProcessingService audioProcessing, IFFmpegInstallation ffmpegInstallation) : MetadataBase<AudioConverterSettings>
    {
        private readonly Logger _logger = logger;
        private readonly Lazy<ITagService> _tagService = tagService;
        private readonly IAudioProcessingService _audioProcessing = audioProcessing;
        private readonly IFFmpegInstallation _ffmpegInstallation = ffmpegInstallation;

        public override string Name => "Codec Tinker";

        public override MetadataFile FindMetadataFile(Artist artist, string path) => default!;

        public override MetadataFileResult ArtistMetadata(Artist artist) => default!;

        public override MetadataFileResult AlbumMetadata(Artist artist, Album album, string albumPath) => default!;

        public override List<ImageFileResult> ArtistImages(Artist artist) => [];

        public override List<ImageFileResult> AlbumImages(Artist artist, Album album, string albumFolder) => [];

        public override List<ImageFileResult> TrackImages(Artist artist, TrackFile trackFile) => [];

        public override MetadataFileResult TrackMetadata(Artist artist, TrackFile trackFile)
        {
            if (trackFile.Id != 0)
                return null!;

            ConvertTrack(trackFile).GetAwaiter().GetResult();
            return null!;
        }

        private record TrackAudio(AudioFormat Format, int? Bitrate, int? BitDepth, int? SampleRate)
        {
            public int? Quality => AudioFormatHelper.IsLossyFormat(Format) ? Bitrate : BitDepth;
        }

        private async Task ConvertTrack(TrackFile trackFile)
        {
            TrackAudio? track = await ProbeTrackAsync(trackFile.Path);
            if (track == null)
                return;

            ConversionRule? rule = FindRule(trackFile, track);
            ConversionResult result;
            if (rule != null)
            {
                if (rule.TargetFormat == AudioFormat.Unknown)
                {
                    _logger.Debug($"Skipping conversion due to no-conversion artist tag for {trackFile.Artist?.Value?.Name}");
                    return;
                }

                result = ShouldBlockConversion(rule, track);
            }
            else if (IsFormatEnabledForConversion(track.Format))
            {
                result = ConversionResult.Success((AudioFormat)Settings.TargetFormat);
            }
            else
            {
                _logger.Trace($"No rule matched for {trackFile.OriginalFilePath}");
                return;
            }

            if (result.IsBlocked)
                return;

            if (result.TargetSampleRate.HasValue)
            {
                result.TargetSampleRate = track.SampleRate.HasValue ? Math.Min(track.SampleRate.Value, result.TargetSampleRate.Value) : null;

                if (!result.TargetBitDepth.HasValue && !AudioFormatHelper.IsLossyFormat(result.TargetFormat) && track.BitDepth is 16 or 24 or 32)
                    result.TargetBitDepth = track.BitDepth;
            }

            if (rule?.TargetSampleRate.HasValue == true &&
                result.TargetFormat == track.Format &&
                (!result.TargetSampleRate.HasValue || result.TargetSampleRate == track.SampleRate) &&
                (!result.TargetBitDepth.HasValue || result.TargetBitDepth == track.BitDepth))
            {
                _logger.Debug($"Track already matches {rule}, nothing to convert: {trackFile.Path}");
                return;
            }

            _logger.Debug($"Converting {Describe(track.Format, track.Bitrate, track.BitDepth, track.SampleRate)} to {Describe(result.TargetFormat, result.TargetBitrate, result.TargetBitDepth, result.TargetSampleRate)}: {trackFile.Path}");

            await PerformConversion(trackFile, result);
        }

        private async Task<TrackAudio?> ProbeTrackAsync(string path)
        {
            AudioFormat format = await GetTrackAudioFormatAsync(path);
            if (format == AudioFormat.Unknown)
                return null;

            int? bitrate = await _audioProcessing.GetAudioBitrateAsync(path);
            int? bitDepth = AudioFormatHelper.IsLossyFormat(format) ? null : await _audioProcessing.GetAudioBitDepthAsync(path);
            int? sampleRate = await _audioProcessing.GetAudioSampleRateAsync(path);
            _logger.Trace($"Track {path}: {Describe(format, bitrate, bitDepth, sampleRate)}");
            return new TrackAudio(format, bitrate, bitDepth, sampleRate);
        }

        private async Task PerformConversion(TrackFile trackFile, ConversionResult result)
        {
            AudioFileContext audioFile = new(trackFile.Path);
            bool success = await _audioProcessing.ConvertToFormatAsync(audioFile, result.TargetFormat, result.TargetBitrate, result.TargetBitDepth, result.UseCBR, result.TargetSampleRate);
            trackFile.Path = audioFile.FilePath;

            if (success)
                _logger.Info($"Successfully converted track: {trackFile.Path}");
            else
                _logger.Warn($"Failed to convert track: {trackFile.Path}");
        }

        private ConversionResult ShouldBlockConversion(ConversionRule rule, TrackAudio track)
        {
            if (rule.TargetFormat == AudioFormat.Unknown)
                return ConversionResult.Blocked();

            // Block lossy to lossless conversion
            if (AudioFormatHelper.IsLossyFormat(track.Format) && !AudioFormatHelper.IsLossyFormat(rule.TargetFormat))
            {
                _logger.Warn($"Blocked lossy to lossless conversion from {track.Format} to {rule.TargetFormat}");
                return ConversionResult.Blocked();
            }

            // Block bitrate upsampling for lossy formats
            if (AudioFormatHelper.IsLossyFormat(track.Format) &&
                AudioFormatHelper.IsLossyFormat(rule.TargetFormat) &&
                track.Bitrate.HasValue &&
                rule.TargetBitrate.HasValue &&
                rule.TargetBitrate.Value > track.Bitrate.Value)
            {
                _logger.Warn($"Blocked bitrate upsampling from {track.Bitrate}kbps to {rule.TargetBitrate}kbps for {track.Format}");
                return ConversionResult.Blocked();
            }

            // Block bit depth upsampling for lossless formats
            if (!AudioFormatHelper.IsLossyFormat(track.Format) &&
                !AudioFormatHelper.IsLossyFormat(rule.TargetFormat) &&
                track.BitDepth.HasValue &&
                rule.TargetBitDepth.HasValue &&
                rule.TargetBitDepth.Value > track.BitDepth.Value)
            {
                _logger.Warn($"Blocked bit depth upsampling from {track.BitDepth}-bit to {rule.TargetBitDepth}-bit for {track.Format}");
                return ConversionResult.Blocked();
            }

            return ConversionResult.FromRule(rule);
        }

        private ConversionRule? FindRule(TrackFile trackFile, TrackAudio track)
        {
            foreach (ConversionRule artistRule in GetArtistTagRules(trackFile))
            {
                if (IsRuleMatching(artistRule, track))
                {
                    _logger.Debug($"Using artist tag rule for {trackFile.Artist?.Value?.Name}: {artistRule}");
                    return artistRule;
                }
            }

            foreach (KeyValuePair<string, string> ruleEntry in Settings.CustomConversion)
            {
                if (RuleParser.TryParseRule(ruleEntry.Key, ruleEntry.Value, out ConversionRule rule) && IsRuleMatching(rule, track))
                {
                    _logger.Debug($"Matched conversion rule: {rule}");
                    return rule;
                }
            }

            return null;
        }

        private List<ConversionRule> GetArtistTagRules(TrackFile trackFile)
        {
            if (trackFile.Artist?.Value?.Tags == null || trackFile.Artist.Value.Tags.Count == 0)
                return [];

            List<ConversionRule> rules = [];
            foreach (Tag? tag in trackFile.Artist.Value.Tags.Select(x => _tagService.Value.GetTag(x)))
            {
                if (RuleParser.TryParseArtistTag(tag.Label, out ConversionRule rule))
                    rules.Add(rule);
            }
            return rules;
        }

        private static bool IsRuleMatching(ConversionRule rule, TrackAudio track) =>
            rule.MatchesFormat(track.Format) && rule.MatchesSource(track.Quality, track.SampleRate);

        private async Task<AudioFormat> GetTrackAudioFormatAsync(string trackPath)
        {
            string extension = Path.GetExtension(trackPath);

            // For .m4a files, use codec detection since they can contain AAC or ALAC
            if (string.Equals(extension, ".m4a", StringComparison.OrdinalIgnoreCase))
            {
                AudioFormat detectedFormat = await _audioProcessing.DetectAudioFormatAsync(trackPath);
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

        private static string Describe(AudioFormat format, int? bitrate, int? bitDepth, int? sampleRate)
        {
            List<string> details = [];
            if (bitrate > 0)
                details.Add($"{bitrate}kbps");
            if (bitDepth.HasValue)
                details.Add($"{bitDepth}-bit");
            if (sampleRate.HasValue)
                details.Add($"{sampleRate} Hz");
            return details.Count > 0 ? $"{format} ({string.Join(", ", details)})" : format.ToString();
        }

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

        public new ValidationResult Test()
        {
            List<ValidationFailure> failures = [];

            if (!_ffmpegInstallation.IsInstalled())
                failures.Add(new ValidationFailure(string.Empty,
                    "FFmpeg is not available. Set up and test the 'FFmpeg' provider in the Metadata settings to download it before enabling Codec Tinker."));
            else
                _logger.Debug("FFmpeg found at {0}", _ffmpegInstallation.ExecutablesDirectory);

            return new ValidationResult(failures);
        }

    }
}