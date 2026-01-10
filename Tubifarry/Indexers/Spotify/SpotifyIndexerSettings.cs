using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Validation;
using Tubifarry.Indexers.YouTube;

namespace Tubifarry.Indexers.Spotify
{
    public class SpotifyIndexerSettingsValidator : AbstractValidator<SpotifyIndexerSettings>
    {
        public SpotifyIndexerSettingsValidator()
        {
            // Include YouTube validation rules for YouTube Music authentication
            Include(new YouTubeIndexerSettingsValidator());

            // Spotify-specific validation rules
            RuleFor(x => x.MaxSearchResults)
                .GreaterThan(0)
                .LessThanOrEqualTo(50)
                .WithMessage("Max search results must be between 1 and 50");

            RuleFor(x => x.MaxEnrichmentAttempts)
                .GreaterThan(0)
                .LessThanOrEqualTo(20)
                .WithMessage("Max enrichment attempts must be between 1 and 20");

            RuleFor(x => x.TrackCountTolerance)
                .GreaterThanOrEqualTo(0)
                .LessThanOrEqualTo(50)
                .WithMessage("Track count tolerance must be between 0 and 50");

            RuleFor(x => x.YearTolerance)
                .GreaterThanOrEqualTo(0)
                .LessThanOrEqualTo(50)
                .WithMessage("Year tolerance must be between 0 and 50");

            RuleFor(x => x.CustomSpotifyClientId)
                .NotEmpty()
                .When(x => !string.IsNullOrWhiteSpace(x.CustomSpotifyClientSecret))
                .WithMessage("Custom Spotify Client ID must be provided when Custom Client Secret is set");

            RuleFor(x => x.CustomSpotifyClientSecret)
                .NotEmpty()
                .When(x => !string.IsNullOrWhiteSpace(x.CustomSpotifyClientId))
                .WithMessage("Custom Spotify Client Secret must be provided when Custom Client ID is set");
        }
    }

    public class SpotifyIndexerSettings : YouTubeIndexerSettings
    {
        private static readonly SpotifyIndexerSettingsValidator Validator = new();

        [FieldDefinition(10, Label = "Max Search Results", Type = FieldType.Number, HelpText = "Maximum number of results to fetch from Spotify for each search.", Advanced = true)]
        public int MaxSearchResults { get; set; } = 20;

        [FieldDefinition(11, Label = "Max Enrichment Attempts", Type = FieldType.Number, HelpText = "Maximum number of YouTube Music albums to check for each Spotify album.", Advanced = true)]
        public int MaxEnrichmentAttempts { get; set; } = 7;

        [FieldDefinition(12, Label = "Enable Fuzzy Matching", Type = FieldType.Checkbox, HelpText = "This can help match albums with slight spelling differences but may occasionally match incorrect albums.", Advanced = true)]
        public bool EnableFuzzyMatching { get; set; } = true;

        [FieldDefinition(13, Label = "Track Count Tolerance", Type = FieldType.Number, HelpText = "Percentage tolerance for track count differences between Spotify and YouTube Music.", Advanced = true)]
        public int TrackCountTolerance { get; set; } = 20;

        [FieldDefinition(14, Label = "Year Tolerance", Type = FieldType.Number, HelpText = "Number of years tolerance for release date differences between Spotify and YouTube Music.", Advanced = true)]
        public int YearTolerance { get; set; } = 2;

        [FieldDefinition(15, Label = "Spotify Client ID", Type = FieldType.Textbox, HelpText = "This allows you to use your own Spotify API rate limit quota instead of the shared one. Get your credentials from https://developer.spotify.com/dashboard", Advanced = true)]
        public string CustomSpotifyClientId { get; set; } = string.Empty;

        [FieldDefinition(16, Label = "Spotify Client Secret", Type = FieldType.Password, HelpText = "Client ID and Secret must be provided together to use your own credentials.", Advanced = true)]
        public string CustomSpotifyClientSecret { get; set; } = string.Empty;

        public override NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}