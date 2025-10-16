using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Validation;
using Tubifarry.Core.Utilities;

namespace Tubifarry.ImportLists.ArrStack
{
    public class ArrSoundtrackImportSettingsValidator : AbstractValidator<ArrSoundtrackImportSettings>
    {
        public ArrSoundtrackImportSettingsValidator()
        {
            // Base URL validation
            RuleFor(c => c.BaseUrl)
                .NotEmpty()
                .WithMessage("Base URL is required")
                .ValidRootUrl()
                .Must(url => !url.EndsWith('/'))
                .WithMessage("Base URL must not end with a slash");

            // API Key validation
            RuleFor(c => c.ApiKey)
                .NotEmpty()
                .WithMessage("API key is required")
                .MinimumLength(25)
                .WithMessage("API key must be at least 25 characters");

            // When using Permanent cache, require a valid CacheDirectory
            RuleFor(x => x.CacheDirectory)
                .NotEmpty()
                .Must((settings, path) => (settings.RequestCacheType != (int)CacheType.Permanent) || (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)))
                .WithMessage("A valid Cache Directory is required for Permanent caching.");

            // Validate the system stability for Memory cache
            RuleFor(x => x.RequestCacheType)
                .Must((type) => (type == (int)CacheType.Permanent) || Tubifarry.AverageRuntime > TimeSpan.FromDays(4) ||
                           (DateTime.UtcNow - Tubifarry.LastStarted) > TimeSpan.FromDays(5))
                .When(x => x.RequestCacheType == (int)CacheType.Memory)
                .WithMessage("The system is not detected as stable. Please wait for the system to stabilize or use permanent cache.");

            // Cache Retention validation
            RuleFor(c => c.CacheRetentionDays)
                .GreaterThanOrEqualTo(1)
                .WithMessage("Cache retention must be at least 1 day");

            // API Endpoints validation
            RuleFor(c => c.APIItemEndpoint)
                .NotEmpty()
                .WithMessage("API Item Endpoint is required")
                .Must(endpoint => endpoint.StartsWith('/'))
                .WithMessage("API Item Endpoint must start with '/'")
                .Must(endpoint => endpoint.Contains("/api/"))
                .WithMessage("API Item Endpoint must contain '/api/'");

            RuleFor(c => c.APIStatusEndpoint)
                .NotEmpty()
                .WithMessage("API Status Endpoint is required")
                .Must(endpoint => endpoint.StartsWith('/'))
                .WithMessage("API Status Endpoint must start with '/'")
                .Must(endpoint => endpoint.Contains("/api/"))
                .WithMessage("API Status Endpoint must contain '/api/'");

            // Refresh Interval validation
            RuleFor(c => c.RefreshInterval)
                .GreaterThanOrEqualTo(0.5)
                .WithMessage("Refresh interval must be at least 0.5 hours");
        }
    }

    public class ArrSoundtrackImportSettings : IImportListSettings
    {
        private static readonly ArrSoundtrackImportSettingsValidator Validator = new();

        [FieldDefinition(0, Label = "Base URL", Type = FieldType.Url, HelpText = "The base URL of your Arr application", Placeholder = "http://localhost:7878")]
        public string BaseUrl { get; set; } = string.Empty;

        [FieldDefinition(1, Label = "API Key", Type = FieldType.Textbox, HelpText = "API key from your Arr application settings (General tab)", Placeholder = "Enter your API key")]
        public string ApiKey { get; set; } = string.Empty;

        [FieldDefinition(2, Label = "API Item Endpoint", Type = FieldType.Textbox, HelpText = "API endpoint for fetching media items", Advanced = true, Placeholder = "/api/v3/movie")]
        public string APIItemEndpoint { get; set; } = string.Empty;

        [FieldDefinition(3, Label = "API Status Endpoint", Type = FieldType.Textbox, HelpText = "API endpoint for system status", Advanced = true, Placeholder = "/api/v3/system/status")]
        public string APIStatusEndpoint { get; set; } = string.Empty;

        [FieldDefinition(4, Label = "Use Strict Search", Type = FieldType.Checkbox, HelpText = "Use strict MusicBrainz search. When disabled, may return more results but lower accuracy", Advanced = true)]
        public bool UseStrongMusicBrainzSearch { get; set; } = true;

        [FieldDefinition(5, Label = "Cache Type", Type = FieldType.Select, SelectOptions = typeof(CacheType), HelpText = "Select Memory (non-permanent) or Permanent caching")]
        public int RequestCacheType { get; set; } = (int)CacheType.Permanent;

        [FieldDefinition(6, Label = "Cache Directory", Type = FieldType.Path, HelpText = "Directory for caching MusicBrainz results", Placeholder = "/config/soundtrack-cache")]
        public string CacheDirectory { get; set; } = string.Empty;

        [FieldDefinition(7, Label = "Cache Retention", Type = FieldType.Number, HelpText = "How many days to keep cached MusicBrainz results", Unit = "Days", Advanced = true, Placeholder = "7")]
        public int CacheRetentionDays { get; set; } = 7;

        [FieldDefinition(8, Label = "Refresh Interval", Type = FieldType.Textbox, HelpText = "The interval to refresh the import list. Fractional values are allowed (e.g., 1.5 for 1 hour and 30 minutes).", Unit = "hours", Advanced = true, Placeholder = "12")]
        public double RefreshInterval { get; set; } = 12.0;

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}