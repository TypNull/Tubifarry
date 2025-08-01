using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace Tubifarry.Notifications.Queue
{
    public class QueueCleanerSettingsValidator : AbstractValidator<QueueCleanerSettings>
    {
        public QueueCleanerSettingsValidator() { }
    }

    public class QueueCleanerSettings : IProviderConfig
    {
        private static readonly QueueCleanerSettingsValidator Validator = new();

        [FieldDefinition(1, Label = "Blocklist Option", Type = FieldType.Select, SelectOptions = typeof(BlocklistOptions), HelpText = "Specify how to handle blocklisting during queue cleaning.")]
        public int BlocklistOption { get; set; } = (int)BlocklistOptions.RemoveAndBlocklist;

        [FieldDefinition(2, Label = "Rename Option", Type = FieldType.Select, SelectOptions = typeof(RenameOptions), HelpText = "Specify how to handle renaming during queue cleaning.", HelpTextWarning = "Only enable this option if you plan to rename your songs again later (e.g., during Lidarr import), as it may disrupt the current naming structure.")]
        public int RenameOption { get; set; } = (int)RenameOptions.DoNotRename;

        [FieldDefinition(3, Label = "Import Cleaning Option", Type = FieldType.Select, SelectOptions = typeof(ImportCleaningOptions), HelpText = "Specify how to handle import cleaning during queue cleaning.")]
        public int ImportCleaningOption { get; set; } = (int)ImportCleaningOptions.Always;

        [FieldDefinition(4, Label = "Retry Finding Release", Type = FieldType.Checkbox, HelpText = "Retry searching for the release if the import fails during queue cleaning.")]
        public bool RetryFindingRelease { get; set; } = true;

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }

    public enum BlocklistOptions
    {
        [FieldOption(Label = "Remove and Blocklist", Hint = "Remove the album and add it to the blocklist to prevent future imports.")]
        RemoveAndBlocklist,

        [FieldOption(Label = "Remove Only", Hint = "Remove the album without adding it to the blocklist.")]
        RemoveOnly,

        [FieldOption(Label = "Blocklist Only", Hint = "Add the album to the blocklist without removing it.")]
        BlocklistOnly
    }

    public enum RenameOptions
    {
        [FieldOption(Label = "Do Not Rename", Hint = "No renaming will be performed on the album folder or tracks during import.")]
        DoNotRename,

        [FieldOption(Label = "Rename Tracks", Hint = "Rename the album tracks based on available metadata, then retry the import process.")]
        RenameTracks
    }

    public enum ImportCleaningOptions
    {
        [FieldOption(Label = "Disabled", Hint = "No cleaning or organization will be performed during import.")]
        Disabled,

        [FieldOption(Label = "When Missing Tracks", Hint = "Clean the album if it has missing tracks.")]
        WhenMissingTracks,

        [FieldOption(Label = "When Album Info Incomplete", Hint = "Clean the album if the metadata is incomplete or insufficient.")]
        WhenAlbumInfoIncomplete,

        [FieldOption(Label = "Always", Hint = "Clean the album, regardless of metadata or track completeness.")]
        Always
    }
}