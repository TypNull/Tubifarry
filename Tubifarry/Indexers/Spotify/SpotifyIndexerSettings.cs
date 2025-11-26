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

        public override NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}