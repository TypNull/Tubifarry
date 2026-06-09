using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace Tubifarry.Metadata.Lyrics
{
    public class LyricsEnhancerSettingsValidator : AbstractValidator<LyricsEnhancerSettings>
    {
        public LyricsEnhancerSettingsValidator()
        {
            // Validate LRCLIB instance URL if enabled
            RuleFor(x => x.LrcLibInstanceUrl)
                .NotEmpty()
                .When(x => x.LrcLibEnabled)
                .WithMessage("LRCLIB instance URL is required when LRCLIB provider is enabled")
                .Must(url => Uri.IsWellFormedUriString(url, UriKind.Absolute))
                .When(x => x.LrcLibEnabled && !string.IsNullOrEmpty(x.LrcLibInstanceUrl))
                .WithMessage("LRCLIB instance URL must be a valid URL");

            // Validate Genius API key if enabled
            RuleFor(x => x.GeniusApiKey)
                .NotEmpty()
                .When(x => x.GeniusEnabled)
                .WithMessage("Genius API key is required when Genius provider is enabled");

            // Validate at least one provider is enabled
            RuleFor(x => x)
                .Must(x => x.LrcLibEnabled || x.GeniusEnabled || x.BinimumEnabled || x.LyricsPlusEnabled || x.UnisonEnabled)
                .WithMessage("At least one lyrics provider must be enabled");

            // Validate UpdateInterval when scheduled updates are enabled
            RuleFor(x => x.UpdateInterval)
                .GreaterThanOrEqualTo(7)
                .When(x => x.EnableScheduledUpdates)
                .WithMessage("Update interval must be at least 1 week");
        }
    }

    public class LyricsEnhancerSettings : IProviderConfig
    {
        private static readonly LyricsEnhancerSettingsValidator Validator = new();

        [FieldDefinition(0, Label = "Create LRC Files", Type = FieldType.Select, SelectOptions = typeof(LyricOptions), Section = MetadataSectionType.Metadata, HelpText = "Choose what kind of LRC files to create")]
        public int LrcFileOptions { get; set; } = (int)LyricOptions.OnlyLineSynced;

        [FieldDefinition(1, Label = "Line Synced File Type", Type = FieldType.Select, SelectOptions = typeof(LineSyncedFileType), Section = MetadataSectionType.Metadata, HelpText = "File format for plain text and line-synced lyrics")]
        public int LineSyncedFileTypeOption { get; set; } = (int)LineSyncedFileType.Lrc;

        [FieldDefinition(2, Label = "Word Synced File Type", Type = FieldType.Select, SelectOptions = typeof(WordSyncedFileType), Section = MetadataSectionType.Metadata, HelpText = "File format for word-synced lyrics")]
        public int WordSyncedFileTypeOption { get; set; } = (int)WordSyncedFileType.Elrc;

        [FieldDefinition(3, Label = "Lyrics Embedding", Type = FieldType.Select, SelectOptions = typeof(LyricOptions), Section = MetadataSectionType.Metadata, HelpText = "Choose how to embed lyrics in audio files metadata")]
        public int LyricEmbeddingOption { get; set; } = (int)LyricOptions.Disabled;

        [FieldDefinition(4, Label = "Overwrite Existing LRC Files", Type = FieldType.Checkbox, Section = MetadataSectionType.Metadata, HelpText = "Overwrite existing LRC files")]
        public bool OverwriteExistingLrcFiles { get; set; }

        // LRCLIB Provider settings
        [FieldDefinition(5, Label = "Enable LRCLIB", Type = FieldType.Checkbox, Section = MetadataSectionType.Metadata, HelpText = "Use LRCLIB as a lyrics provider (provides synced lyrics)")]
        public bool LrcLibEnabled { get; set; }

        [FieldDefinition(6, Label = "LRCLIB Instance URL", Type = FieldType.Url, Section = MetadataSectionType.Metadata, HelpText = "URL of the LRCLIB instance to use", Placeholder = "https://lrclib.net")]
        public string LrcLibInstanceUrl { get; set; } = "https://lrclib.net";

        // Genius Provider settings
        [FieldDefinition(7, Label = "Enable Genius", Type = FieldType.Checkbox, Section = MetadataSectionType.Metadata, HelpText = "Use Genius as a lyrics provider (text only, no synced lyrics)")]
        public bool GeniusEnabled { get; set; }

        [FieldDefinition(8, Label = "Genius API Key", Type = FieldType.Textbox, Section = MetadataSectionType.Metadata, HelpText = "Your Genius API key", Privacy = PrivacyLevel.ApiKey)]
        public string GeniusApiKey { get; set; } = "";

        // Binimum Provider settings (ISRC-keyed Apple Music TTML cache)
        [FieldDefinition(9, Label = "Enable Binimum", Type = FieldType.Checkbox, Section = MetadataSectionType.Metadata, HelpText = "Use Binimum as a lyrics provider (Apple Music TTML, requires the track's ISRC)")]
        public bool BinimumEnabled { get; set; }

        [FieldDefinition(10, Label = "Binimum API URL", Type = FieldType.Url, Section = MetadataSectionType.Metadata, HelpText = "URL of the Binimum lyrics API", Placeholder = "https://lyrics-api.binimum.org", Hidden = HiddenType.Hidden)]
        public string BinimumApiUrl { get; set; } = "https://lyrics-api.binimum.org";

        // LyricsPlus Provider settings (Apple Music + QQ, word-synced)
        [FieldDefinition(11, Label = "Enable LyricsPlus", Type = FieldType.Checkbox, Section = MetadataSectionType.Metadata, HelpText = "Use LyricsPlus as a lyrics provider (word-synced lyrics from Apple Music and QQ Music)")]
        public bool LyricsPlusEnabled { get; set; }

        [FieldDefinition(12, Label = "LyricsPlus URL", Type = FieldType.Url, Section = MetadataSectionType.Metadata, HelpText = "URL of the LyricsPlus (KPoe) instance", Placeholder = "https://lyrics.geeked.wtf", Hidden = HiddenType.Hidden)]
        public string LyricsPlusUrl { get; set; } = "https://lyrics.geeked.wtf";

        // Unison Provider settings (crowdsourced TTML/LRC)
        [FieldDefinition(13, Label = "Enable Unison", Type = FieldType.Checkbox, Section = MetadataSectionType.Metadata, HelpText = "Use Unison as a lyrics provider (crowdsourced synced lyrics; small catalog)")]
        public bool UnisonEnabled { get; set; }

        [FieldDefinition(14, Label = "Unison URL", Type = FieldType.Url, Section = MetadataSectionType.Metadata, HelpText = "URL of the Unison API instance", Placeholder = "https://unison.boidu.dev", Hidden = HiddenType.Hidden)]
        public string UnisonUrl { get; set; } = "https://unison.boidu.dev";

        // Scheduled Update Settings
        [FieldDefinition(15, Label = "Enable Scheduled Updates", Type = FieldType.Checkbox, Section = MetadataSectionType.Metadata, HelpText = "Enable automatic scheduled updates to refresh lyrics for existing files")]
        public bool EnableScheduledUpdates { get; set; }

        [FieldDefinition(16, Label = "Update Interval", Type = FieldType.Number, Unit = "days", Section = MetadataSectionType.Metadata, HelpText = "How often to run scheduled lyrics updates.")]
        public int UpdateInterval { get; set; } = 7;

        public LyricsEnhancerSettings() => Instance = this;

        public static LyricsEnhancerSettings? Instance { get; private set; }

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }

    /// <summary>
    /// Command for scheduled lyrics update task.
    /// </summary>
    public class LyricsUpdateCommand : Command
    {
        public override bool SendUpdatesToClient => true;

        public override bool UpdateScheduledTask => true;

        public override string CompletionMessage => _completionMessage ?? "Lyrics update completed";
        private string? _completionMessage;

        public void SetCompletionMessage(string message) => _completionMessage = message;
    }

    public enum LyricOptions
    {
        [FieldOption(Label = "Disabled", Hint = "Disabled")]
        Disabled,

        [FieldOption(Label = "Only Plain", Hint = "Use plain text lyrics if available.")]
        OnlyPlain,

        [FieldOption(Label = "Only Line Synced", Hint = "Use line-synced lyrics if available.")]
        OnlyLineSynced,

        [FieldOption(Label = "Prefer Line Synced", Hint = "Use line-synced lyrics if available, fall back to plain text.")]
        PreferLineSynced,

        [FieldOption(Label = "Only Word Synced", Hint = "Use word-synced lyrics if available.")]
        OnlyWordSynced,

        [FieldOption(Label = "Prefer Word Synced", Hint = "Use word-synced lyrics if available, fall back to line-synced or plain text.")]
        PreferWordSynced
    }

    public enum WordSyncedFileType
    {
        [FieldOption(Label = "ELRC", Hint = "Enhanced LRC format with word-level timestamps.")]
        Elrc,

        [FieldOption(Label = "TTML", Hint = "Timed Text Markup Language format (Apple Music style).")]
        Ttml,

        [FieldOption(Label = "Lyricsfile", Hint = "YAML-based open lyrics format with word-by-word sync support (LRCGET/LRCLIB).")]
        Lyricsfile
    }

    public enum LineSyncedFileType
    {
        [FieldOption(Label = "LRC", Hint = "Standard LRC file format for line-synced and plain text lyrics.")]
        Lrc,

        [FieldOption(Label = "Use Same as Word Synced", Hint = "Use the same file type as configured for word-synced lyrics.")]
        UseSameAsWordSynced
    }
}