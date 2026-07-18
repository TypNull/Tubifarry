using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Metadata.Converter
{
    public class AudioConverterSettingsValidator : AbstractValidator<AudioConverterSettings>
    {
        public AudioConverterSettingsValidator()
        {
            // Validate custom conversion rules
            RuleFor(x => x.CustomConversion)
                .Must(customConversions => customConversions?.All(IsValidConversionRule) != false)
                .WithMessage("Custom conversion rules must be in the format 'source -> target' (e.g., mp3 to flac).");

            RuleFor(x => x.CustomConversion)
                .Must(customConversions => customConversions?.All(IsValidLossyConversion) != false)
                .WithMessage("Lossy formats cannot be converted to non-lossy formats.");

            RuleFor(x => x.CustomConversion)
                .Must(customConversions => customConversions?.All(IsValidCBRUsage) != false)
                .WithMessage("CBR flag is only applicable to lossy formats. Lossless formats are inherently variable bitrate.");

            RuleFor(x => x)
                .Must(settings => IsValidStaticConversion(settings))
                .WithMessage("Lossy formats cannot be converted to non-lossy formats.");
        }

        private bool IsValidConversionRule(KeyValuePair<string, string> rule)
        {
            if (string.IsNullOrWhiteSpace(rule.Key) || string.IsNullOrWhiteSpace(rule.Value))
                return false;

            return RuleParser.TryParseRule(rule.Key, rule.Value, out _);
        }

        private bool IsValidLossyConversion(KeyValuePair<string, string> rule)
        {
            if (!RuleParser.TryParseRule(rule.Key, rule.Value, out ConversionRule parsedRule))
                return false;

            if (parsedRule.IsGlobalRule)
                return true;

            if (AudioFormatHelper.IsLossyFormat(parsedRule.SourceFormat) &&
                !AudioFormatHelper.IsLossyFormat(parsedRule.TargetFormat))
                return false;

            return true;
        }


        private bool IsValidCBRUsage(KeyValuePair<string, string> rule)
        {
            if (!RuleParser.TryParseRule(rule.Key, rule.Value, out ConversionRule parsedRule))
                return false;

            // If CBR is specified, target must be a lossy format
            if (parsedRule.UseCBR && !AudioFormatHelper.IsLossyFormat(parsedRule.TargetFormat))
                return false;

            return true;
        }

        private static bool IsValidStaticConversion(AudioConverterSettings settings) =>
            AudioFormatHelper.IsLossyFormat((AudioFormat)settings.TargetFormat) || (!settings.ConvertMP3 && !settings.ConvertAAC && !settings.ConvertOpus && !settings.ConvertOther);

    }

    public class AudioConverterSettings : IProviderConfig
    {
        private static readonly AudioConverterSettingsValidator Validator = new();

        [FieldDefinition(1, Label = "Convert MP3", Type = FieldType.Checkbox, Section = MetadataSectionType.Metadata, HelpText = "Convert MP3 files.")]
        public bool ConvertMP3 { get; set; }

        [FieldDefinition(2, Label = "Convert AAC", Type = FieldType.Checkbox, Section = MetadataSectionType.Metadata, HelpText = "Convert AAC files.")]
        public bool ConvertAAC { get; set; }

        [FieldDefinition(3, Label = "Convert FLAC", Type = FieldType.Checkbox, Section = MetadataSectionType.Metadata, HelpText = "Convert FLAC files.")]
        public bool ConvertFLAC { get; set; }

        [FieldDefinition(4, Label = "Convert WAV", Type = FieldType.Checkbox, Section = MetadataSectionType.Metadata, HelpText = "Convert WAV files.")]
        public bool ConvertWAV { get; set; }

        [FieldDefinition(5, Label = "Convert Opus", Type = FieldType.Checkbox, Section = MetadataSectionType.Metadata, HelpText = "Convert Opus files.")]
        public bool ConvertOpus { get; set; }

        [FieldDefinition(7, Label = "Convert Other Formats", Type = FieldType.Checkbox, Section = MetadataSectionType.Metadata, HelpText = "Convert other formats (e.g., WMA).")]
        public bool ConvertOther { get; set; }

        [FieldDefinition(8, Label = "Target Format", Type = FieldType.Select, SelectOptions = typeof(TargetAudioFormat), Section = MetadataSectionType.Metadata, HelpText = "Select the target format to convert audio files into.")]
        public int TargetFormat { get; set; } = (int)TargetAudioFormat.Opus;

        [FieldDefinition(9, Label = "Custom Conversion Rules", Type = FieldType.KeyValueList, Section = MetadataSectionType.Metadata, HelpText = "Custom conversion rules. Examples: 'flac -> mp3:320:cbr' (FLAC to CBR MP3), 'mp3:320 -> mp3:128' (downsample), 'flac:24 -> flac:16' (reduce bit depth). Add ':cbr' for constant bitrate encoding. Upsampling is blocked automatically.")]
        public IEnumerable<KeyValuePair<string, string>> CustomConversion { get; set; } = [];

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }

    public enum TargetAudioFormat
    {
        [FieldOption(Label = "AAC", Hint = "Convert to AAC format.")]
        AAC = 1,

        [FieldOption(Label = "MP3", Hint = "Convert to MP3 format.")]
        MP3 = 2,

        [FieldOption(Label = "Opus", Hint = "Convert to Opus format.")]
        Opus = 3,

        [FieldOption(Label = "FLAC", Hint = "Convert to FLAC format.")]
        FLAC = 5,
    }
}