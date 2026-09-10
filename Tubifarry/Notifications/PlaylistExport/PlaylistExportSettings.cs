using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Validation;
using System.Text.Json;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Notifications.PlaylistExport;

public class PlaylistExportSettingsValidator : AbstractValidator<PlaylistExportSettings>
{
    public PlaylistExportSettingsValidator() =>
        RuleFor(x => x.OutputPath)
            .NotEmpty()
            .When(x => !x.AutoDetectOutputPath)
            .WithMessage("Output path is required when auto-detect is disabled");
}

public class PlaylistExportSettings : DynamicStateSettings
{
    private static readonly PlaylistExportSettingsValidator Validator = new();

    [FieldDefinition(1, Label = "Output Path", Type = FieldType.Path, HelpText = "Directory where .m3u8 playlist files will be written. Ignored when auto-detect is enabled.")]
    public string OutputPath { get; set; } = "";

    [FieldDefinition(2, Label = "Auto-Detect Output Path", Type = FieldType.Checkbox, HelpText = "Automatically use the common root of your music library as the output directory. Overrides Output Path.")]
    public bool AutoDetectOutputPath { get; set; }

    [FieldDefinition(3, Label = "Use Relative Paths", Type = FieldType.Checkbox, HelpText = "Write paths in the .m3u8 files relative to the output directory instead of absolute paths.")]
    public bool UseRelativePaths { get; set; }

    [FieldDefinition(4, Label = "Clean Up Playlist on Removal", Type = FieldType.Checkbox, HelpText = "Delete the .m3u8 file when the corresponding import list is removed from Lidarr.")]
    public bool CleanupOnRemove { get; set; }

    [FieldDefinition(5, Label = "Track Mode", Type = FieldType.Select, SelectOptions = typeof(PlaylistTrackMode), HelpText = "Controls whether playlists are generated from album-level or track-level data.")]
    public int TrackMode { get; set; } = (int)PlaylistTrackMode.PreferTrackData;

    [FieldDefinition(6, Label = "Minimum Interval", Type = FieldType.Textbox, Unit = "hours", Advanced = true, HelpText = "Shortest time between two generations. Generation compares every item of every selected list against the library, so it is not run on each import or each list sync.", Placeholder = "6")]
    public double MinimumInterval { get; set; } = 6.0;

    public PlaylistTrackMode GetTrackMode() => (PlaylistTrackMode)TrackMode;

    public IEnumerable<int> GetSelectedListIds()
    {
        Dictionary<string, bool> states =
            JsonSerializer.Deserialize<Dictionary<string, bool>>(
                string.IsNullOrEmpty(StateJson) ? "{}" : StateJson) ?? [];

        return states
            .Where(kv => kv.Key.StartsWith("list_") && kv.Value)
            .Select(kv => int.TryParse(kv.Key[5..], out int id) ? id : -1)
            .Where(id => id > 0);
    }

    public override NzbDroneValidationResult Validate() => new(Validator.Validate(this));
}

public enum PlaylistTrackMode
{
    [FieldOption(Label = "Album Data Only", Hint = "Always generate a playlist for matched albums.")]
    AlbumDataOnly = 0,

    [FieldOption(Label = "Prefer Track Data", Hint = "Use per-track data for lists that support it; fall back to album data for those that don't.")]
    PreferTrackData = 1,

    [FieldOption(Label = "Track Data Only", Hint = "Only generate playlists for lists that support per-track data.")]
    TrackDataOnly = 2,
}

