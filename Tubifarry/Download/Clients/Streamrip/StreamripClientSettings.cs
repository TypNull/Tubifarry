using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;

namespace Tubifarry.Download.Clients.Streamrip
{
    public class StreamripClientSettingsValidator : AbstractValidator<StreamripClientSettings>
    {
        public StreamripClientSettingsValidator()
        {
            // Download Path validation
            RuleFor(x => x.DownloadPath).IsValidPath();
            // Max Parallel Downloads validation
            RuleFor(x => x.MaxParallelDownloads).InclusiveBetween(1, 5);
        }
    }

    public class StreamripClientSettings : IProviderConfig
    {
        private static readonly StreamripClientSettingsValidator Validator = new();

        [FieldDefinition(0, Label = "Download Path", Type = FieldType.Path, HelpText = "Folder where streamrip saves albums before Lidarr imports them.")]
        public string DownloadPath { get; set; } = string.Empty;

        [FieldDefinition(1, Label = "Max Parallel Downloads", Type = FieldType.Number, HelpText = "Albums downloaded at the same time. Each one runs in its own Python process.")]
        public int MaxParallelDownloads { get; set; } = 2;

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}
