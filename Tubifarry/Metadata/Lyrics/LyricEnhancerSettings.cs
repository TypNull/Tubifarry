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
            RuleFor(x => new { x.LrcLibEnabled, x.GeniusEnabled })
                .Must(x => x.LrcLibEnabled || x.GeniusEnabled)
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


        [FieldDefinition(0, Label = "Create LRC Files", Type = FieldType.Checkbox, Section = MetadataSectionType.Metadata, HelpText = "Create synchronized LRC files when available")]
        public bool CreateLrcFiles { get; set; }

        [FieldDefinition(1, Label = "Embed Lyrics in Audio Files", Type = FieldType.Checkbox, Section = MetadataSectionType.Metadata, HelpText = "Embed plain lyrics in audio files metadata")]
        public bool EmbedLyricsInAudioFiles { get; set; }

        [FieldDefinition(2, Label = "Overwrite Existing LRC Files", Type = FieldType.Checkbox, Section = MetadataSectionType.Metadata, HelpText = "Overwrite existing LRC files")]
        public bool OverwriteExistingLrcFiles { get; set; }

        // LRCLIB Provider settings
        [FieldDefinition(3, Label = "Enable LRCLIB", Type = FieldType.Checkbox, Section = MetadataSectionType.Metadata, HelpText = "Use LRCLIB as a lyrics provider (provides synced lyrics)")]
        public bool LrcLibEnabled { get; set; }

        [FieldDefinition(4, Label = "LRCLIB Instance URL", Type = FieldType.Url, Section = MetadataSectionType.Metadata, HelpText = "URL of the LRCLIB instance to use", Placeholder = "https://lrclib.net")]
        public string LrcLibInstanceUrl { get; set; } = "https://lrclib.net";

        // Genius Provider settings
        [FieldDefinition(5, Label = "Enable Genius", Type = FieldType.Checkbox, Section = MetadataSectionType.Metadata, HelpText = "Use Genius as a lyrics provider (text only, no synced lyrics)")]
        public bool GeniusEnabled { get; set; }

        [FieldDefinition(6, Label = "Genius API Key", Type = FieldType.Textbox, Section = MetadataSectionType.Metadata, HelpText = "Your Genius API key", Privacy = PrivacyLevel.ApiKey)]
        public string GeniusApiKey { get; set; } = "";

        // Scheduled Update Settings
        [FieldDefinition(7, Label = "Enable Scheduled Updates", Type = FieldType.Checkbox, Section = MetadataSectionType.Metadata, HelpText = "Enable automatic scheduled updates to refresh lyrics for existing files")]
        public bool EnableScheduledUpdates { get; set; }

        [FieldDefinition(8, Label = "Update Interval", Type = FieldType.Number, Unit = "days", Section = MetadataSectionType.Metadata, HelpText = "How often to run scheduled lyrics updates.")]
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
}