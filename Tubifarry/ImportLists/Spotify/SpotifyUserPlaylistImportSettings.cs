using FluentValidation;
using NzbDrone.Core.Annotations;

namespace NzbDrone.Core.ImportLists.Spotify
{
    public class SpotifyUserPlaylistImportSettingsValidator : SpotifySettingsBaseValidator<SpotifyUserPlaylistImportSettings>
    {
        public SpotifyUserPlaylistImportSettingsValidator() : base()
        {
            RuleFor(c => c.RefreshInterval)
                .GreaterThanOrEqualTo(2)
                .WithMessage("Refresh interval must be at least 0.5 hours.");

            RuleFor(c => c.CacheDirectory)
           .Must(path => string.IsNullOrEmpty(path) || Directory.Exists(path))
           .WithMessage("Cache directory must be a valid path or empty.");

            RuleFor(c => c.CacheRetentionDays)
                .GreaterThanOrEqualTo(1)
                .WithMessage("Retention time must be at least 1 day.");
        }
    }

    public class SpotifyUserPlaylistImportSettings : SpotifySettingsBase<SpotifyUserPlaylistImportSettings>
    {
        protected override AbstractValidator<SpotifyUserPlaylistImportSettings> Validator => new SpotifySettingsBaseValidator<SpotifyUserPlaylistImportSettings>();

        public override string Scope => "playlist-read-private";

        [FieldDefinition(1, Label = "Refresh Interval", Type = FieldType.Textbox, HelpText = "The interval in hours to refresh the import list. Fractional values are allowed (e.g., 1.5 for 1 hour and 30 minutes).", Advanced = true, Placeholder = "12")]
        public double RefreshInterval { get; set; } = 12.0;

        [FieldDefinition(2, Label = "Cache Directory", Type = FieldType.Path, HelpText = "The directory where cached data will be stored. If left empty, no cache will be used.", Placeholder = "/config/spotify-cache")]
        public string CacheDirectory { get; set; } = string.Empty;

        [FieldDefinition(3, Label = "Skip Cached Playlists", Type = FieldType.Checkbox, HelpText = "If enabled, playlists that are already cached will not be searched on MusicBrainz or re-imported as long as the cache is valid. Disabling this option will force a re-search on MusicBrainz.", Advanced = true)]
        public bool SkipCachedPlaylists { get; set; } = true;

        [FieldDefinition(4, Label = "Cache Retention Time", Type = FieldType.Number, HelpText = "The number of days to retain cached data.", Advanced = true, Placeholder = "7")]
        public int CacheRetentionDays { get; set; } = 7;
    }
}
