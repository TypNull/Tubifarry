using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;

namespace Tubifarry.Download.Clients.DABMusic
{
    public class DABMusicProviderSettingsValidator : AbstractValidator<DABMusicProviderSettings>
    {
        public DABMusicProviderSettingsValidator()
        {
            // Validate DownloadPath
            RuleFor(x => x.DownloadPath)
                .IsValidPath()
                .WithMessage("Download path must be a valid directory.");

            // Validate BaseUrl
            RuleFor(x => x.BaseUrl)
                .NotEmpty().WithMessage("Base URL is required.")
                .Must(url => Uri.IsWellFormedUriString(url, UriKind.Absolute))
                .WithMessage("Base URL must be a valid URL.");

            // Validate Email
            RuleFor(x => x.Email)
                .EmailAddress().WithMessage("Must be a valid email address.")
                .When(x => !string.IsNullOrWhiteSpace(x.Email));

            // Validate ConnectionRetries
            RuleFor(x => x.ConnectionRetries)
                .GreaterThanOrEqualTo(1)
                .LessThanOrEqualTo(10)
                .WithMessage("Connection retries must be between 1 and 10.");

            // Validate MaxParallelDownloads
            RuleFor(x => x.MaxParallelDownloads)
                .GreaterThanOrEqualTo(1)
                .LessThanOrEqualTo(5)
                .WithMessage("Max parallel downloads must be between 1 and 5.");

            // Validate MaxDownloadSpeed
            RuleFor(x => x.MaxDownloadSpeed)
                .GreaterThanOrEqualTo(0)
                .WithMessage("Max download speed must be greater than or equal to 0.")
                .LessThanOrEqualTo(100_000)
                .WithMessage("Max download speed must be less than or equal to 100 MB/s.");
        }
    }

    /// <summary>
    /// Configuration settings for the DABMusic download client
    /// </summary>
    public class DABMusicProviderSettings : IProviderConfig
    {
        private static readonly DABMusicProviderSettingsValidator Validator = new();

        [FieldDefinition(0, Label = "Download Path", Type = FieldType.Path, HelpText = "Directory where downloaded files will be saved")]
        public string DownloadPath { get; set; } = string.Empty;

        [FieldDefinition(1, Label = "Base URL", Type = FieldType.Textbox, HelpText = "URL of the DABMusic API instance", Placeholder = "https://dabmusic.xyz")]
        public string BaseUrl { get; set; } = "https://dabmusic.xyz";

        [FieldDefinition(2, Label = "Email", Type = FieldType.Textbox, HelpText = "Your DABMusic account email (optional for now)")]
        public string Email { get; set; } = "";

        [FieldDefinition(3, Label = "Password", Type = FieldType.Password, HelpText = "Your DABMusic account password (optional for now)", Privacy = PrivacyLevel.Password)]
        public string Password { get; set; } = "";

        [FieldDefinition(4, Type = FieldType.Number, Label = "Connection Retries", HelpText = "Number of times to retry failed connections", Advanced = true)]
        public int ConnectionRetries { get; set; } = 3;

        [FieldDefinition(5, Type = FieldType.Number, Label = "Max Parallel Downloads", HelpText = "Maximum number of downloads that can run simultaneously")]
        public int MaxParallelDownloads { get; set; } = 2;

        [FieldDefinition(6, Label = "Max Download Speed", Type = FieldType.Number, HelpText = "Set to 0 for unlimited speed. Limits download speed per file.", Unit = "KB/s", Advanced = true)]
        public int MaxDownloadSpeed { get; set; } = 0;

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}