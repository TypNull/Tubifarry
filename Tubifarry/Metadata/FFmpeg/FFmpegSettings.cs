using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace Tubifarry.Metadata.FFmpeg
{
    public class FFmpegSettingsValidator : AbstractValidator<FFmpegSettings>
    {
        public FFmpegSettingsValidator()
        {
            RuleFor(x => x.InstallDirectory)
                .Must(path => string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
                .WithMessage("Install directory must be an absolute path or empty.");
        }
    }

    public class FFmpegSettings : IProviderConfig
    {
        private static readonly FFmpegSettingsValidator Validator = new();

        [FieldDefinition(0, Label = "Install Directory", Type = FieldType.Path, Section = MetadataSectionType.Metadata, Placeholder = "/config/plugins/TypNull/Tubifarry/ffmpeg", HelpText = "Directory containing the FFmpeg and FFprobe binaries. Leave empty to use the plugin directory.")]
        public string InstallDirectory { get; set; } = string.Empty;

        [FieldDefinition(1, Label = "Download Automatically", Type = FieldType.Checkbox, Section = MetadataSectionType.Metadata, HelpText = "Download the pinned FFmpeg release into the install directory when no usable binary is found. Disable if FFmpeg is provided by the system, PATH or the FFMPEG environment variable.")]
        public bool AutoDownload { get; set; } = true;

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}
