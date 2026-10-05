using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using Tubifarry.Core.Python;

namespace Tubifarry.Metadata.Beets
{
    public class BeetsSettingsValidator : AbstractValidator<BeetsSettings>
    {
        public BeetsSettingsValidator()
        {
            // Library Path validation
            RuleFor(x => x.LibraryPath)
                .NotEmpty()
                .Must(Path.IsPathRooted)
                .WithMessage("Library path must be an absolute path.");

            // Config Path validation
            RuleFor(x => x.ConfigPath)
                .Must(path => string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
                .WithMessage("Configuration file must be an absolute path or empty.");

            // Extra Packages validation
            RuleFor(x => x.ExtraPackages)
                .Must(packages => ParsePackages(packages).All(package => !package.StartsWith('-')))
                .WithMessage("Extra packages must be package names, not options.");

            // Sync Interval validation
            RuleFor(x => x.SyncInterval)
                .InclusiveBetween(0, 24 * 30)
                .WithMessage("Sync interval must be between 0 (off) and 720 hours.");

            // Install Directory validation
            RuleFor(x => x.InstallDirectory)
                .Must(path => string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
                .WithMessage("Install directory must be an absolute path or empty.");
        }

        public static IEnumerable<string> ParsePackages(string? packages) =>
            (packages ?? string.Empty).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public class BeetsSettings : IProviderConfig
    {
        private static readonly BeetsSettingsValidator Validator = new();

        [FieldDefinition(0, Label = "Library Path", Type = FieldType.Path, Section = MetadataSectionType.Metadata, HelpText = "Folder for the beets library database (beets.db), or the database file itself.")]
        public string LibraryPath { get; set; } = string.Empty;

        [FieldDefinition(1, Label = "Configuration File", Type = FieldType.FilePath, Section = MetadataSectionType.Metadata, HelpText = "Optional beets config.yaml. Leave empty to use the beets defaults.")]
        public string ConfigPath { get; set; } = string.Empty;

        [FieldDefinition(2, Label = "Extra Packages", Type = FieldType.Textbox, Section = MetadataSectionType.Metadata, HelpText = "Additional Python packages for beets plugins, separated by commas (e.g. pyacoustid, pylast).")]
        public string ExtraPackages { get; set; } = string.Empty;

        [FieldDefinition(3, Label = "Import On Album Import", Type = FieldType.Checkbox, Section = MetadataSectionType.Metadata, HelpText = "Add every album Lidarr imports to the beets library.")]
        public bool ImportOnAlbumImport { get; set; } = true;

        [FieldDefinition(4, Label = "Sync Interval", Type = FieldType.Number, Unit = "hours", Section = MetadataSectionType.Metadata, HelpText = "Run the beets sync as a scheduled task. 0 turns it off.")]
        public int SyncInterval { get; set; }

        [FieldDefinition(5, Label = "Catch Up Missing Albums", Type = FieldType.Checkbox, Section = MetadataSectionType.Metadata, HelpText = "The sync adds albums Lidarr already has but beets does not know yet.")]
        public bool CatchUpMissingAlbums { get; set; } = true;

        [FieldDefinition(6, Label = "Allow Retagging", Type = FieldType.Checkbox, Section = MetadataSectionType.Metadata, HelpText = "The sync refreshes MusicBrainz data in beets and writes changed tags into the files.", HelpTextWarning = "This rewrites the tags of your music files.")]
        public bool AllowRetagging { get; set; }

        [FieldDefinition(7, Label = "Install Directory", Type = FieldType.Path, Section = MetadataSectionType.Metadata, Advanced = true, HelpText = "Folder for uv, the managed Python and the beets environment. Leave empty to use the plugin directory.")]
        public string InstallDirectory { get; set; } = string.Empty;

        public PythonEnvironmentSpec ToEnvironmentSpec() => new(
            "beets",
            PythonEnvironments.DefaultPythonVersion,
            ["beets>=2.14,<3", "pyacoustid>=1.3", .. BeetsSettingsValidator.ParsePackages(ExtraPackages)],
            string.IsNullOrWhiteSpace(InstallDirectory) ? null : InstallDirectory,
            ["beets", "acoustid"]);

        public string ResolveDatabasePath() =>
            LibraryPath.EndsWith(".db", StringComparison.OrdinalIgnoreCase) ? LibraryPath : Path.Combine(LibraryPath, "beets.db");

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}
