using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Validation;

namespace Tubifarry.ImportLists.ListenBrainz.ListenBrainzRecommendations
{
    public class ListenBrainzRecommendationsSettingsValidator : AbstractValidator<ListenBrainzRecommendationsSettings>
    {
        public ListenBrainzRecommendationsSettingsValidator()
        {
            RuleFor(c => c.UserName)
                .NotEmpty()
                .WithMessage("ListenBrainz username is required");

            RuleFor(c => c.MaxPlaylists)
                .GreaterThan(0)
                .LessThanOrEqualTo(100)
                .WithMessage("Max playlists must be between 1 and 100");

            RuleFor(c => c.RefreshInterval)
                .GreaterThanOrEqualTo(1)
                .WithMessage("Refresh interval must be at least 1 day");
        }
    }

    public class ListenBrainzRecommendationsSettings : IImportListSettings
    {
        private static readonly ListenBrainzRecommendationsSettingsValidator Validator = new();

        public ListenBrainzRecommendationsSettings()
        {
            BaseUrl = "https://api.listenbrainz.org";
            RefreshInterval = 7;
            MaxPlaylists = 25; // Default to 25 recommendation playlists
        }

        public string BaseUrl { get; set; }

        [FieldDefinition(0, Label = "ListenBrainz Username", HelpText = "The ListenBrainz username to fetch recommendation playlists from", Placeholder = "username")]
        public string UserName { get; set; } = string.Empty;

        [FieldDefinition(1, Label = "User Token", Type = FieldType.Password, HelpText = "Optional ListenBrainz user token for authenticated requests (higher rate limits)", Advanced = true)]
        public string UserToken { get; set; } = string.Empty;

        [FieldDefinition(2, Label = "Max Playlists", Type = FieldType.Number, HelpText = "Maximum number of recommendation playlists to fetch and process (1-100)")]
        public int MaxPlaylists { get; set; }

        [FieldDefinition(3, Label = "Refresh Interval", Type = FieldType.Textbox, HelpText = "Interval between refreshes in days", Unit = "days", Advanced = true)]
        public double RefreshInterval { get; set; }

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}