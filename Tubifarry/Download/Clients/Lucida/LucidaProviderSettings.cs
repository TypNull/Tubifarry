using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;

namespace Tubifarry.Download.Clients.Lucida
{
    public class LucidaProviderSettingsValidator : AbstractValidator<LucidaProviderSettings>
    {
        public LucidaProviderSettingsValidator()
        {
            // Validate DownloadPath
            RuleFor(x => x.DownloadPath)
                .IsValidPath()
                .WithMessage("Download path must be a valid directory.");

            // Validate RequestTimeout
            RuleFor(x => x.RequestTimeout)
                .GreaterThanOrEqualTo(5)
                .LessThanOrEqualTo(300)
                .WithMessage("Request timeout must be between 5 and 300 seconds.");

            // Validate ConnectionRetries
            RuleFor(x => x.ConnectionRetries)
                .GreaterThanOrEqualTo(1)
                .LessThanOrEqualTo(10)
                .WithMessage("Connection retries must be between 1 and 10.");

            // Validate MaxParallelDownloads
            RuleFor(x => x.MaxParallelDownloads)
                .GreaterThanOrEqualTo(1)
                .LessThanOrEqualTo(10)
                .WithMessage("Max parallel downloads must be between 1 and 10.");

            // Validate MaxDownloadSpeed
            RuleFor(x => x.MaxDownloadSpeed)
                .GreaterThanOrEqualTo(0)
                .WithMessage("Max download speed must be greater than or equal to 0.")
                .LessThanOrEqualTo(100_000)
                .WithMessage("Max download speed must be less than or equal to 100 MB/s.");
        }
    }

    /// <summary>
    /// Configuration settings for the Lucida download client
    /// </summary>
    public class LucidaProviderSettings : IProviderConfig
    {
        private static readonly LucidaProviderSettingsValidator Validator = new();

        [FieldDefinition(0, Label = "Download Path", Type = FieldType.Path, HelpText = "Directory where downloaded files will be saved")]
        public string DownloadPath { get; set; } = string.Empty;

        [FieldDefinition(2, Type = FieldType.Number, Label = "Request Timeout", Unit = "seconds", HelpText = "Timeout for HTTP requests to Lucida", Advanced = true)]
        public int RequestTimeout { get; set; } = 30;

        [FieldDefinition(3, Type = FieldType.Number, Label = "Connection Retries", HelpText = "Number of times to retry failed connections", Advanced = true)]
        public int ConnectionRetries { get; set; } = 3;

        [FieldDefinition(4, Type = FieldType.Number, Label = "Max Parallel Downloads", HelpText = "Maximum number of downloads that can run simultaneously")]
        public int MaxParallelDownloads { get; set; } = 2;

        [FieldDefinition(5, Label = "Max Download Speed", Type = FieldType.Number, HelpText = "Set to 0 for unlimited speed. Limits download speed per file.", Unit = "KB/s", Advanced = true)]
        public int MaxDownloadSpeed { get; set; }

        public string? BaseUrl { get; set; }

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}
