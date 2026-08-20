using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;
using Tubifarry.Download.Clients.YouTube;

namespace Tubifarry.Download.Clients.Invidious
{
    public class InvidiousProviderSettingsValidator : AbstractValidator<InvidiousProviderSettings>
    {
        public InvidiousProviderSettingsValidator()
        {
            RuleFor(x => x.DownloadPath)
                .IsValidPath()
                .WithMessage("Download path must be a valid directory.");

            RuleFor(x => x.BaseUrl)
                .NotEmpty().WithMessage("Base URL is required.")
                .Must(url => Uri.IsWellFormedUriString(url, UriKind.Absolute))
                .WithMessage("Base URL must be a valid URL.");

            RuleFor(x => x.MaxDownloadSpeed)
                .GreaterThanOrEqualTo(0)
                .WithMessage("Max download speed must be greater than or equal to 0.")
                .LessThanOrEqualTo(100_000)
                .WithMessage("Max download speed must be less than or equal to 100 MB/s.");

            RuleFor(x => x.SponsorBlockApiEndpoint)
                .NotEmpty()
                .When(x => x.UseSponsorBlock)
                .WithMessage("SponsorBlock API endpoint is required when SponsorBlock is enabled.")
                .Must(url => Uri.IsWellFormedUriString(url, UriKind.Absolute))
                .When(x => x.UseSponsorBlock && !string.IsNullOrEmpty(x.SponsorBlockApiEndpoint))
                .WithMessage("SponsorBlock API endpoint must be a valid URL.");
        }
    }

    public class InvidiousProviderSettings : IProviderConfig
    {
        private static readonly InvidiousProviderSettingsValidator Validator = new();

        public InvidiousProviderSettings()
        {
            MaxDownloadSpeed = 0;
            ConnectionRetries = 3;
        }

        [FieldDefinition(0, Label = "Download Path", Type = FieldType.Path, HelpText = "Directory where downloaded files will be saved")]
        public string DownloadPath { get; set; } = string.Empty;

        [FieldDefinition(1, Label = "Base URL", Type = FieldType.Textbox, HelpText = "URL of your Invidious instance", Placeholder = "http://localhost:3000")]
        public string BaseUrl { get; set; } = string.Empty;

        [FieldDefinition(2, Label = "Proxy Videos", Type = FieldType.Checkbox, HelpText = "Proxy video streams through Invidious instead of connecting directly to YouTube")]
        public bool ProxyVideos { get; set; } = true;

        [FieldDefinition(3, Label = "ReEncode", Type = FieldType.Select, SelectOptions = typeof(ReEncodeOptions), HelpText = "Specify whether to re-encode audio files and how to handle FFmpeg")]
        public int ReEncode { get; set; } = (int)ReEncodeOptions.Disabled;

        [FieldDefinition(4, Label = "Use ID3v2.3 Tags", HelpText = "Enable for better compatibility with older media players", Type = FieldType.Checkbox, Advanced = true)]
        public bool UseID3v2_3 { get; set; }

        [FieldDefinition(5, Label = "Use SponsorBlock", Type = FieldType.Checkbox, HelpText = "Remove non-music segments (intros, outros, talking) from downloaded tracks")]
        public bool UseSponsorBlock { get; set; }

        [FieldDefinition(6, Label = "SponsorBlock API Endpoint", Type = FieldType.Textbox, Placeholder = "https://sponsor.ajay.app", HelpText = "SponsorBlock API endpoint URL. Change only if using a custom instance.", Advanced = true)]
        public string SponsorBlockApiEndpoint { get; set; } = "https://sponsor.ajay.app";

        [FieldDefinition(7, Label = "Max Download Speed", Type = FieldType.Number, HelpText = "Set to 0 for unlimited speed. Limits download speed per file.", Unit = "KB/s", Advanced = true)]
        public int MaxDownloadSpeed { get; set; }

        [FieldDefinition(8, Type = FieldType.Number, Label = "Max Parallel Downloads", HelpText = "Maximum number of downloads that can run simultaneously")]
        public int MaxParallelDownloads { get; set; } = 1;

        [FieldDefinition(9, Type = FieldType.Number, Label = "Connection Retries", HelpText = "Number of times to retry failed connections", Advanced = true)]
        public int ConnectionRetries { get; set; }

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}
