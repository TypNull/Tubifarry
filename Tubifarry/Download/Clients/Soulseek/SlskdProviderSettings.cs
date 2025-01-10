using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Download.Clients.Soulseek
{
    internal class SlskdProviderSettingsValidator : AbstractValidator<SlskdProviderSettings>
    {
        public SlskdProviderSettingsValidator()
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

            // Validate DownloadPath can be null or empty, or a valid directory path
            RuleFor(x => x.DownloadPath)
                .Must(path => string.IsNullOrEmpty(path) || Path.IsPathRooted(path))
                .WithMessage("Download path must be a valid directory path.")
                .When(x => !string.IsNullOrEmpty(x.DownloadPath));
        }
    }

    public class SlskdProviderSettings : IProviderConfig
    {
        private static readonly SlskdProviderSettingsValidator Validator = new();

        [FieldDefinition(0, Label = "URL", Type = FieldType.Url, Placeholder = "http://localhost:5030", HelpText = "The URL of your Slskd instance.")]
        public string BaseUrl { get; set; } = "http://localhost:5030";

        [FieldDefinition(1, Label = "External URL", Type = FieldType.Url, Placeholder = "https://slskd.example.com", HelpText = "An optional external URL for additional resources or documentation.", Advanced = true)]
        public string? ExternalUrl { get; set; } = string.Empty;

        [FieldDefinition(2, Label = "API Key", Type = FieldType.Textbox, Privacy = PrivacyLevel.ApiKey, HelpText = "The API key for your Slskd instance. You can find or set this in the Slskd's settings under 'Options'.", Placeholder = "Enter your API key")]
        public string ApiKey { get; set; } = string.Empty;

        [FieldDefinition(3, Label = "Download Path", Type = FieldType.Path, HelpText = "Specify the directory where downloaded files will be saved. If not specified, Slskd's default download path is used.")]
        public string DownloadPath { get; set; } = string.Empty;

        [FieldDefinition(4, Label = "Is Fetched remote", Type = FieldType.Checkbox, Hidden = HiddenType.Hidden)]
        public bool IsRemotePath { get; set; }

        [FieldDefinition(4, Label = "Is Localhost", Type = FieldType.Checkbox, Hidden = HiddenType.Hidden)]
        public bool IsLocalhost { get; set; }

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}
