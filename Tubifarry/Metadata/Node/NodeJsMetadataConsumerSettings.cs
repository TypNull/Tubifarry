using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace Tubifarry.Metadata.Node
{
    /// <summary>
    /// Validator for Node.js metadata consumer settings.
    /// </summary>
    public class NodeJsMetadataConsumerSettingsValidator : AbstractValidator<NodeJsMetadataConsumerSettings>
    {
        public NodeJsMetadataConsumerSettingsValidator()
        {
            // Validate Script path (optional)
            RuleFor(x => x.ScriptPath)
                .Must(path => string.IsNullOrEmpty(path) || File.Exists(path))
                .WithMessage("Script file must exist if specified")
                .Must(IsValidJavaScriptFile)
                .WithMessage("Script file must be a valid JavaScript file (.js)");

            // Validate Output path (optional)
            RuleFor(x => x.OutputPath)
                .Must(path => string.IsNullOrEmpty(path) ||
                     Directory.Exists(Path.GetDirectoryName(path)) ||
                     Path.IsPathRooted(path))
                .WithMessage("Output directory must be valid or empty");

            // Validate Required packages
            RuleFor(x => x.RequiredPackages)
                .Must(BeValidPackageList)
                .WithMessage("Package names must be valid npm package identifiers");
        }

        /// <summary>
        /// Validates that the provided file path points to a valid JavaScript file
        /// </summary>
        private bool IsValidJavaScriptFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return true; // Optional field

            if (!File.Exists(filePath))
                return false;

            return filePath.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
                   filePath.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Validates that package names are valid npm package identifiers
        /// </summary>
        private bool BeValidPackageList(string packages)
        {
            if (string.IsNullOrWhiteSpace(packages))
                return true;

            IEnumerable<string> packageNames = packages
                .Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim());

            foreach (string package in packageNames)
            {
                // Basic npm package name validation
                if (string.IsNullOrWhiteSpace(package) ||
                    package.Contains(" ") ||
                    package.StartsWith(".") ||
                    package.StartsWith("_"))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Settings for the Node.js metadata consumer.
    /// </summary>
    public class NodeJsMetadataConsumerSettings : IProviderConfig
    {
        private static readonly NodeJsMetadataConsumerSettingsValidator Validator = new();

        [FieldDefinition(0, Label = "Node.js Script Path", Type = FieldType.FilePath, Section = MetadataSectionType.Metadata, HelpText = "Path to custom Node.js script file (.js). Leave empty to use built-in script.")]
        public string ScriptPath { get; set; } = "";

        [FieldDefinition(1, Label = "Output Path", Type = FieldType.Path, Section = MetadataSectionType.Metadata, HelpText = "Directory where Node.js script should output metadata files")]
        public string OutputPath { get; set; } = "";

        [FieldDefinition(2, Label = "Required NPM Packages", Type = FieldType.Textbox, Section = MetadataSectionType.Metadata, HelpText = "Comma-separated list of NPM packages to install (e.g., 'music-metadata,node-id3')")]
        public string RequiredPackages { get; set; } = "music-metadata,fs-extra";

        [FieldDefinition(3, Label = "Node.js Version", Type = FieldType.Textbox, Section = MetadataSectionType.Metadata, HelpText = "Specific Node.js version to use (e.g., '24.5.0'). Leave empty for latest LTS.")]
        public string NodeVersion { get; set; } = "";

        [FieldDefinition(4, Label = "Script Timeout (seconds)", Type = FieldType.Number, Section = MetadataSectionType.Metadata, HelpText = "Maximum time to wait for script execution")]
        public int ScriptTimeout { get; set; } = 300;

        /// <summary>
        /// Validates the settings.
        /// </summary>
        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}