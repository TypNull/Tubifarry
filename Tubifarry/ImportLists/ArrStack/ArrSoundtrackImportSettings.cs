using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;

namespace NzbDrone.Core.ImportLists.ArrStack
{
    public class ArrSoundtrackImportSettingsValidator : AbstractValidator<ArrSoundtrackImportSettings>
    {
        public ArrSoundtrackImportSettingsValidator()
        {
            // Validate BaseUrl
            RuleFor(c => c.BaseUrl)
                .ValidRootUrl()
                .Must(url => !url.EndsWith("/"))
                .WithMessage("Base URL must not end with a slash ('/').");

            // Validate ApiKey
            RuleFor(c => c.ApiKey)
                .NotEmpty()
                .Must(key => key.Length > 25)
                .WithMessage("API key must be longer than 25 characters.");

            // Validate CacheDirectory
            RuleFor(c => c.CacheDirectory)
                .IsValidPath();

            // Validate CacheRetentionDays
            RuleFor(c => c.CacheRetentionDays)
                .GreaterThanOrEqualTo(1)
                .WithMessage("Retention time must be at least 1 day.");

            // Validate APIMovieEndpoint
            RuleFor(c => c.APIItemEndpoint)
                .Must(endpoint => endpoint.StartsWith("/"))
                .WithMessage("API Item Endpoint must start with a slash ('/').");

            // Validate APIStatusEndpoint (if needed)
            RuleFor(c => c.APIStatusEndpoint)
                .Must(endpoint => endpoint.StartsWith("/"))
                .WithMessage("API Status Endpoint must start with a slash ('/').");

            // Validate RefreshInterval (in hours, allowing fractional values)
            RuleFor(c => c.RefreshInterval)
                .GreaterThanOrEqualTo(0.5)
                .WithMessage("Refresh interval must be at least 0.5 hours.");
        }
    }

    public class ArrSoundtrackImportSettings : IImportListSettings
    {
        private static readonly ArrSoundtrackImportSettingsValidator Validator = new();

        [FieldDefinition(0, Label = "URL", Type = FieldType.Url, HelpText = "The URL of your Arrsapp instance.", Placeholder = "http://localhost:7878/")]
        public string BaseUrl { get; set; } = string.Empty;

        [FieldDefinition(1, Label = "API Key", Type = FieldType.Textbox, HelpText = "The API key for your Radarr instance. You can find this in the App's settings under 'General'.", Placeholder = "Enter your API key")]
        public string ApiKey { get; set; } = string.Empty;

        [FieldDefinition(2, Label = "API Movie Endpoint", Type = FieldType.Textbox, HelpText = "The endpoint for fetching movies from Radarr.", Advanced = true, Placeholder = "/api/v3/movie")]
        public string APIItemEndpoint { get; set; } = string.Empty;

        [FieldDefinition(3, Label = "API Status Endpoint", Type = FieldType.Textbox, HelpText = "The endpoint for fetching system status from Radarr.", Advanced = true, Placeholder = "/api/v3/system/status")]
        public string APIStatusEndpoint { get; set; } = string.Empty;

        [FieldDefinition(4, Label = "Use Strong Search", Type = FieldType.Checkbox, HelpText = "Enable to use a strong-typed search query on MusicBrainz. Disable to allow more lenient searches, which may include audio tracks from movies.", Advanced = true)]
        public bool UseStrongMusicBrainzSearch { get; set; } = true;

        [FieldDefinition(5, Label = "Cache Directory", Type = FieldType.Path, HelpText = "The directory where cached data will be stored.", Placeholder = "/config/soundtrack-cache")]
        public string CacheDirectory { get; set; } = string.Empty;

        [FieldDefinition(6, Label = "Cache Retention Time", Type = FieldType.Number, HelpText = "The number of days to retain cached data.", Advanced = true, Placeholder = "7")]
        public int CacheRetentionDays { get; set; } = 7;

        [FieldDefinition(7, Label = "Refresh Interval", Type = FieldType.Textbox, HelpText = "The interval in hours to refresh the import list. Fractional values are allowed (e.g., 1.5 for 1 hour and 30 minutes).", Advanced = true, Placeholder = "12")]
        public double RefreshInterval { get; set; } = 12.0;

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}