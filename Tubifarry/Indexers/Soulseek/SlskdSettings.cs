using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;
using Tubifarry.Indexers.Soulseek.Search.Templates;

namespace Tubifarry.Indexers.Soulseek
{
    internal class SlskdSettingsValidator : AbstractValidator<SlskdSettings>
    {
        public SlskdSettingsValidator()
        {
            // Base URL validation
            RuleFor(c => c.BaseUrl)
                .ValidRootUrl()
                .Must(url => !url.EndsWith('/'))
                .WithMessage("Base URL must not end with a slash ('/').");

            // External URL validation (only if not empty)
            RuleFor(c => c.ExternalUrl)
                .Must(url => string.IsNullOrEmpty(url) || (Uri.IsWellFormedUriString(url, UriKind.Absolute) && !url.EndsWith('/')))
                .WithMessage("External URL must be a valid URL and must not end with a slash ('/').");

            // API Key validation
            RuleFor(c => c.ApiKey)
                .NotEmpty()
                .WithMessage("API Key is required.");

            // File Limit validation
            RuleFor(c => c.FileLimit)
                .GreaterThanOrEqualTo(1)
                .WithMessage("File Limit must be at least 1.");

            // Maximum Peer Queue Length validation
            RuleFor(c => c.MaximumPeerQueueLength)
                .GreaterThanOrEqualTo(100)
                .WithMessage("Maximum Peer Queue Length must be at least 100.");

            // Minimum Peer Upload Speed validation
            RuleFor(c => c.MinimumPeerUploadSpeed)
                .GreaterThanOrEqualTo(0)
                .WithMessage("Minimum Peer Upload Speed must be a non-negative value.");

            // Minimum Response File Count validation
            RuleFor(c => c.MinimumResponseFileCount)
                .GreaterThanOrEqualTo(1)
                .WithMessage("Minimum Response File Count must be at least 1.");

            // Response Limit validation
            RuleFor(c => c.ResponseLimit)
                .GreaterThanOrEqualTo(1)
                .WithMessage("Response Limit must be at least 1.");

            // Timeout validation
            RuleFor(c => c.TimeoutInSeconds)
                .GreaterThanOrEqualTo(2.0)
                .WithMessage("Timeout must be at least 2 seconds.");

            // TrackFallback validation
            RuleFor(c => c.UseTrackFallback)
                .Equal(false)
                .When(c => !c.UseFallbackSearch)
                .WithMessage("Track Fallback cannot be enabled without Fallback Search.");

            // Results validation
            RuleFor(c => c.MinimumResults)
              .GreaterThanOrEqualTo(0)
              .WithMessage("Minimum Results must be at least 0.");

            // Include File Extensions validation
            RuleFor(c => c.IncludeFileExtensions)
                .Must(extensions => extensions?.All(ext => !ext.Contains('.')) != false)
                .WithMessage("File extensions must not contain a dot ('.').");

            // Search Templates validation
            RuleFor(c => c.SearchTemplates)
                .Must(t => string.IsNullOrWhiteSpace(t) || TemplateEngine.ValidateTemplates(t).Count == 0)
                .WithMessage((_, t) => string.Join("; ", TemplateEngine.ValidateTemplates(t)));

            // Ignore List File Path validation
            RuleFor(c => c.IgnoreListPath)
                .IsValidPath()
                .When(c => !string.IsNullOrWhiteSpace(c.IgnoreListPath))
                .WithMessage("File path must be valid.");
        }
    }

    public class SlskdSettings : IIndexerSettings
    {
        private static readonly SlskdSettingsValidator Validator = new();

        [FieldDefinition(0, Label = "URL", Type = FieldType.Url, Placeholder = "http://localhost:5030", HelpText = "Slskd instance URL")]
        public string BaseUrl { get; set; } = "http://localhost:5030";

        [FieldDefinition(1, Label = "External URL", Type = FieldType.Url, Placeholder = "https://slskd.example.com", HelpText = "Public URL for interactive search links", Advanced = true)]
        public string? ExternalUrl { get; set; } = string.Empty;

        [FieldDefinition(2, Label = "API Key", Type = FieldType.Textbox, Privacy = PrivacyLevel.ApiKey, HelpText = "The API key for your Slskd instance. You can find or set this in the Slskd's settings under 'Options'.", Placeholder = "Enter your API key")]
        public string ApiKey { get; set; } = string.Empty;

        [FieldDefinition(3, Type = FieldType.Checkbox, Label = "Audio Files Only", HelpText = "Return only audio file types")]
        public bool OnlyAudioFiles { get; set; } = true;

        [FieldDefinition(4, Type = FieldType.Tag, Label = "File Extensions", HelpText = "Additional extensions when Audio Files Only is enabled (without dots)", Advanced = true)]
        public IEnumerable<string> IncludeFileExtensions { get; set; } = [];

        [FieldDefinition(6, Type = FieldType.Number, Label = "Early Download Limit", Unit = "days", HelpText = "Days before release to allow downloads", Advanced = true)]
        public int? EarlyReleaseLimit { get; set; } = null;

        [FieldDefinition(7, Type = FieldType.Number, Label = "File Limit", HelpText = "Max files per search", Advanced = true)]
        public int FileLimit { get; set; } = 10000;

        [FieldDefinition(8, Type = FieldType.Number, Label = "Max Peer Queue", HelpText = "Max queued requests per peer", Advanced = true)]
        public int MaximumPeerQueueLength { get; set; } = 1000000;

        private int _minimumPeerUploadSpeedBytes;

        [FieldDefinition(9, Type = FieldType.Number, Label = "Min Peer Speed", Unit = "KB/s", HelpText = "Minimum peer upload speed", Advanced = true)]
        public int MinimumPeerUploadSpeed
        {
            get => _minimumPeerUploadSpeedBytes / 1024;
            set => _minimumPeerUploadSpeedBytes = value * 1024;
        }

        [FieldDefinition(10, Type = FieldType.Number, Label = "Min File Count", HelpText = "Minimum files per response", Advanced = true)]
        public int MinimumResponseFileCount { get; set; } = 1;

        [FieldDefinition(11, Type = FieldType.Checkbox, Label = "Filter Unfitting Albums", HelpText = "Exclude releases with wrong track count", Advanced = true)]
        public bool FilterUnfittingAlbums { get; set; }

        [FieldDefinition(12, Type = FieldType.Number, Label = "Response Limit", HelpText = "Max search responses", Advanced = true)]
        public int ResponseLimit { get; set; } = 100;

        [FieldDefinition(13, Type = FieldType.Number, Label = "Timeout", Unit = "seconds", HelpText = "Search timeout", Advanced = true)]
        public double TimeoutInSeconds { get; set; } = 5;

        [FieldDefinition(14, Type = FieldType.Checkbox, Label = "Append Year", HelpText = "Append the release year to the first search (ignored when templates are set)", Advanced = true)]
        public bool AppendYear { get; set; }

        [FieldDefinition(15, Type = FieldType.Checkbox, Label = "Normalize Search", HelpText = "Remove accents and special characters (é→e, ü→u)", Advanced = true)]
        public bool NormalizedSeach { get; set; }

        [FieldDefinition(16, Type = FieldType.Checkbox, Label = "Volume Variations", HelpText = "Try alternate volume formats (Vol.1 <-> Volume I)", Advanced = true)]
        public bool HandleVolumeVariations { get; set; }

        [FieldDefinition(17, Label = "Enable Fallback Search", Type = FieldType.Checkbox, HelpText = "If no results are found, perform a secondary search using additional metadata.", Advanced = true)]
        public bool UseFallbackSearch { get; set; }

        [FieldDefinition(18, Label = "Track Fallback", Type = FieldType.Checkbox, HelpText = "Search by track names as last resort (requires Fallback Search)", Advanced = true)]
        public bool UseTrackFallback { get; set; }

        [FieldDefinition(19, Type = FieldType.Number, Label = "Minimum Results", HelpText = "Keep searching until this many results found", Advanced = true)]
        public int MinimumResults { get; set; }

        [FieldDefinition(20, Type = FieldType.FilePath, Label = "Ignore List", HelpText = "File with usernames to ignore (one per line)", Advanced = true)]
        public string? IgnoreListPath { get; set; } = string.Empty;

        [FieldDefinition(21, Type = FieldType.Textbox, Label = "Search Templates", HelpText = "Custom search pattern (empty=disabled). Use {{Property}} syntax. Valid: AlbumTitle, AlbumYear, Disambiguation, AlbumQuery, CleanAlbumQuery, Artist.*", Advanced = true)]
        public string? SearchTemplates { get; set; } = string.Empty;

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}