using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Validation;

namespace Tubifarry.ImportLists.ListenBrainz.ListenBrainzPlaylist
{
    public class ListenBrainzPlaylistSettingsValidator : AbstractValidator<ListenBrainzPlaylistSettings>
    {
        public ListenBrainzPlaylistSettingsValidator()
        {
            RuleFor(c => c.AccessToken)
                .NotEmpty()
                .WithMessage("ListenBrainz username is required");
        }
    }

    public class ListenBrainzPlaylistSettings : IImportListSettings
    {
        private static readonly ListenBrainzPlaylistSettingsValidator Validator = new();

        public ListenBrainzPlaylistSettings()
        {
            BaseUrl = "https://api.listenbrainz.org";
            PlaylistIds = Array.Empty<string>();
        }

        public string BaseUrl { get; set; }

        [FieldDefinition(0, Label = "Username", HelpText = "The ListenBrainz username to fetch playlists from", Placeholder = "username")]
        public string AccessToken { get; set; } = string.Empty;

        [FieldDefinition(1, Label = "User Token", Type = FieldType.Password, HelpText = "Optional ListenBrainz user token for authenticated requests (higher rate limits)", Advanced = true)]
        public string UserToken { get; set; } = string.Empty;

        [FieldDefinition(2, Label = "Playlist Type", Type = FieldType.Select, SelectOptions = typeof(ListenBrainzPlaylistEndpointType), HelpText = "Type of playlists to fetch")]
        public int PlaylistType { get; set; }

        [FieldDefinition(3, Label = "Playlists", Type = FieldType.Playlist, HelpText = "Select specific playlists to import")]
        public IEnumerable<string> PlaylistIds { get; set; }

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }

    public enum ListenBrainzPlaylistEndpointType
    {
        [FieldOption(Label = "User Playlists")]
        Normal = 0,

        [FieldOption(Label = "Created-For")]
        CreatedFor = 1,

        [FieldOption(Label = "Recommendations")]
        Recommendations = 2
    }
}
