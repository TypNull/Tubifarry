using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using YamlDotNet.RepresentationModel;

namespace Tubifarry.Metadata.Python
{
    /// <summary>
    /// Validator for Beets metadata consumer settings.
    /// </summary>
    public class BeetsMetadataConsumerSettingsValidator : AbstractValidator<BeetsMetadataConsumerSettings>
    {
        public BeetsMetadataConsumerSettingsValidator()
        {
            // Validate Config path
            RuleFor(x => x.ConfigPath)
                .NotEmpty().WithMessage("Configuration file path is required")
                .Must(path => File.Exists(path))
                .WithMessage("Configuration file must exist")
                .Must(IsValidYamlFile)
                .WithMessage("Configuration file must be a valid YAML file");

            // Validate Library path
            RuleFor(x => x.LibraryPath)
                .NotEmpty().WithMessage("Library database path is required")
                .Must(path => !string.IsNullOrWhiteSpace(path) &&
                     Directory.Exists(Path.GetDirectoryName(path)))
                .WithMessage("Directory for library database must exist");
        }

        /// <summary>
        /// Validates that the provided file path points to a valid YAML file
        /// </summary>
        private bool IsValidYamlFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return false;

            try
            {
                using StreamReader reader = new(filePath);
                YamlStream yaml = new();
                yaml.Load(reader);
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Settings for the Beets metadata consumer.
    /// </summary>
    public class BeetsMetadataConsumerSettings : IProviderConfig
    {
        private static readonly BeetsMetadataConsumerSettingsValidator Validator = new();

        [FieldDefinition(0, Label = "Configuration File", Type = FieldType.FilePath, Section = MetadataSectionType.Metadata, HelpText = "Path to Beets YAML configuration file")]
        public string ConfigPath { get; set; } = "";

        [FieldDefinition(1, Label = "Library Database Path", Type = FieldType.Path, Section = MetadataSectionType.Metadata, HelpText = "Path where Beets should store its library database")]
        public string LibraryPath { get; set; } = "";

        [FieldDefinition(2, Label = "Required Python Packages", Type = FieldType.Textbox, Section = MetadataSectionType.Metadata, HelpText = "Comma-separated list of Python packages to install")]
        public string RequiredPackages { get; set; } = "pyacoustid";

        /// <summary>
        /// Validates the settings.
        /// </summary>
        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}