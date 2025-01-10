using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Indexers.Soulseek
{
    internal class SlskdSettingsValidator : AbstractValidator<SlskdSettings>
    {
        public SlskdSettingsValidator()
        {
            // Base URL validation
            RuleFor(c => c.BaseUrl)
                .ValidRootUrl()
                .Must(url => !url.EndsWith("/"))
                .WithMessage("Base URL must not end with a slash ('/').");

            // External URL validation (only if not empty)
            RuleFor(c => c.ExternalUrl)
                .Must(url => string.IsNullOrEmpty(url) || (Uri.IsWellFormedUriString(url, UriKind.Absolute) && !url.EndsWith("/")))
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

            // Include File Extensions validation
            RuleFor(c => c.IncludeFileExtensions)
                .Must(extensions => extensions == null || extensions.All(ext => !ext.Contains(".")))
                .WithMessage("File extensions must not contain a dot ('.').");
        }
    }

    public class SlskdSettings : IIndexerSettings
    {
        private static readonly SlskdSettingsValidator Validator = new();

        [FieldDefinition(0, Label = "URL", Type = FieldType.Url, Placeholder = "http://localhost:5030", HelpText = "The URL of your Slskd instance.")]
        public string BaseUrl { get; set; } = "http://localhost:5030";

        [FieldDefinition(1, Label = "External URL", Type = FieldType.Url, Placeholder = "https://slskd.example.com", HelpText = "An optional external URL for additional resources or documentation.", Advanced = true)]
        public string? ExternalUrl { get; set; } = string.Empty;

        [FieldDefinition(2, Label = "API Key", Type = FieldType.Textbox, Privacy = PrivacyLevel.ApiKey, HelpText = "The API key for your Slskd instance. You can find or set this in the Slskd's settings under 'Options'.", Placeholder = "Enter your API key")]
        public string ApiKey { get; set; } = string.Empty;

        [FieldDefinition(3, Type = FieldType.Checkbox, Label = "Include Only Audio Files", HelpText = "When enabled, only files with audio extensions will be included in search results. Disabling this option allows all file types.", Advanced = false)]
        public bool OnlyAudioFiles { get; set; } = true;

        [FieldDefinition(4, Type = FieldType.ArtistTag, Label = "Include File Extensions", HelpText = "Specify file extensions to include when 'Include Only Audio Files' is enabled. This setting has no effect if 'Include Only Audio Files' is disabled.", Advanced = true)]
        public IEnumerable<string> IncludeFileExtensions { get; set; } = Array.Empty<string>();

        [FieldDefinition(5, Type = FieldType.Number, Label = "Early Download Limit", Unit = "days", HelpText = "Time before release date Lidarr will download from this indexer, empty is no limit", Advanced = true)]
        public int? EarlyReleaseLimit { get; set; } = null;

        [FieldDefinition(6, Type = FieldType.Number, Label = "File Limit", HelpText = "Maximum number of files to return in a search response.", Advanced = true)]
        public int FileLimit { get; set; } = 10000;

        [FieldDefinition(7, Type = FieldType.Number, Label = "Maximum Peer Queue Length", HelpText = "Maximum number of queued requests allowed per peer.", Advanced = true)]
        public int MaximumPeerQueueLength { get; set; } = 1000000;

        [FieldDefinition(8, Type = FieldType.Number, Label = "Minimum Peer Upload Speed", Unit = "KB/s", HelpText = "Minimum upload speed required for peers (in KB/s).", Advanced = true)]
        public int MinimumPeerUploadSpeed { get; set; } = 0;

        [FieldDefinition(9, Type = FieldType.Number, Label = "Minimum Response File Count", HelpText = "Minimum number of files required in a search response.", Advanced = true)]
        public int MinimumResponseFileCount { get; set; } = 1;

        [FieldDefinition(10, Type = FieldType.Number, Label = "Response Limit", HelpText = "Maximum number of search responses to return.", Advanced = true)]
        public int ResponseLimit { get; set; } = 100;

        [FieldDefinition(11, Type = FieldType.Number, Label = "Timeout", Unit = "seconds", HelpText = "Timeout for search requests in seconds.", Advanced = true)]
        public double TimeoutInSeconds { get; set; } = 5;

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}