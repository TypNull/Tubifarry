using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Validation;

namespace Tubifarry.ImportLists.ListenBrainz.ListenBrainzCreatedForPlaylist
{
    public class ListenBrainzCreatedForPlaylistSettingsValidator : AbstractValidator<ListenBrainzCreatedForPlaylistSettings>
    {
        public ListenBrainzCreatedForPlaylistSettingsValidator()
        {
            RuleFor(c => c.UserName)
                .NotEmpty()
                .WithMessage("ListenBrainz username is required");

            RuleFor(c => c.RefreshInterval)
                .GreaterThanOrEqualTo(1)
                .WithMessage("Refresh interval must be at least 1 day");
        }
    }

    public class ListenBrainzCreatedForPlaylistSettings : IImportListSettings
    {
        private static readonly ListenBrainzCreatedForPlaylistSettingsValidator Validator = new();

        public ListenBrainzCreatedForPlaylistSettings()
        {
            BaseUrl = "https://api.listenbrainz.org";
            RefreshInterval = 7;
            PlaylistType = (int)ListenBrainzPlaylistType.DailyJams;
        }

        public string BaseUrl { get; set; }

        [FieldDefinition(0, Label = "ListenBrainz Username", HelpText = "The ListenBrainz username to fetch playlists from", Placeholder = "username")]
        public string UserName { get; set; } = string.Empty;

        [FieldDefinition(1, Label = "User Token", Type = FieldType.Password, HelpText = "Optional ListenBrainz user token for authenticated requests (higher rate limits)", Advanced = true)]
        public string UserToken { get; set; } = string.Empty;

        [FieldDefinition(2, Label = "Playlist Type", Type = FieldType.Select, SelectOptions = typeof(ListenBrainzPlaylistType), HelpText = "Type of created-for playlist to fetch")]
        public int PlaylistType { get; set; }

        [FieldDefinition(4, Label = "Refresh Interval", Type = FieldType.Textbox, HelpText = "Interval between refreshes in days", Unit = "days", Advanced = true)]
        public double RefreshInterval { get; set; }

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }

    public enum ListenBrainzPlaylistType
    {
        [FieldOption(Label = "Daily Jams")]
        DailyJams = 0,
        [FieldOption(Label = "Weekly Jams")]
        WeeklyJams = 1,
        [FieldOption(Label = "Weekly Exploration")]
        WeeklyExploration = 2,
        [FieldOption(Label = "Weekly New")]
        WeeklyNew = 3,
        [FieldOption(Label = "Monthly Exploration")]
        MonthlyExploration = 4
    }
}