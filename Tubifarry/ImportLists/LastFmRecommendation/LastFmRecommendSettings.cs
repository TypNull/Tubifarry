using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.LastFm;
using NzbDrone.Core.Validation;

namespace Tubifarry.ImportLists.LastFmRecommendation
{
    public class LastFmRecommendSettingsValidator : AbstractValidator<LastFmRecommendSettings>
    {
        public LastFmRecommendSettingsValidator()
        {
            // Validate that the RefreshInterval field is not empty and meets the minimum requirement
            RuleFor(c => c.RefreshInterval)
                .GreaterThanOrEqualTo(5)
                .WithMessage("Refresh interval must be at least 5 days.");

            // Validate that the UserId field is not empty
            RuleFor(c => c.UserId)
                .NotEmpty()
                .WithMessage("Last.fm UserID is required to generate recommendations");

            // Validate that the fetch limit does not exceed 100
            RuleFor(c => c.FetchCount)
                .LessThanOrEqualTo(100)
                .WithMessage("Cannot fetch more than 100 items");

            // Validate that the import limit does not exceed 20
            RuleFor(c => c.ImportCount)
                .LessThanOrEqualTo(20)
                .WithMessage("Maximum recommendation import limit is 20");
        }
    }

    public class LastFmRecommendSettings : IImportListSettings
    {
        private static readonly LastFmRecommendSettingsValidator Validator = new();

        public LastFmRecommendSettings()
        {
            BaseUrl = "https://ws.audioscrobbler.com/2.0/";
            ApiKey = new LastFmUserSettings().ApiKey;
            Method = (int)LastFmRecommendMethodList.TopArtists;
            Period = (int)LastFmUserTimePeriod.Overall;
        }

        // Hidden API configuration
        public string BaseUrl { get; set; }
        public string ApiKey { get; set; }

        [FieldDefinition(0, Label = "Last.fm Username", HelpText = "Your Last.fm username to generate personalized recommendations", Placeholder = "EnterLastFMUsername")]
        public string UserId { get; set; } = string.Empty;

        [FieldDefinition(1, Label = "Refresh Interval", Type = FieldType.Textbox, HelpText = "The interval to refresh the import list. Fractional values are allowed (e.g., 1.5 for 1 day and 12 hours).", Unit = "days", Advanced = true, Placeholder = "7")]
        public double RefreshInterval { get; set; } = 7;

        [FieldDefinition(2, Label = "Recommendation Source", Type = FieldType.Select, SelectOptions = typeof(LastFmRecommendMethodList), HelpText = "Type of listening data to use for recommendations (Top Artists, Albums or Tracks)")]
        public int Method { get; set; }

        [FieldDefinition(3, Label = "Time Range", Type = FieldType.Select, SelectOptions = typeof(LastFmUserTimePeriod), HelpText = "Time period to analyze for generating recommendations (Last week/3 months/6 months/All time)")]
        public int Period { get; set; }

        [FieldDefinition(4, Label = "Fetch Limit", Type = FieldType.Number, HelpText = "Number of results to pull from the top list on Last.fm")]
        public int FetchCount { get; set; } = 25;

        [FieldDefinition(5, Label = "Import Limit", Type = FieldType.Number, HelpText = "Number of recommendations per top list result to actually import to your library")]
        public int ImportCount { get; set; } = 3;

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }

    public enum LastFmRecommendMethodList
    {
        [FieldOption(Label = "Top Artists")]
        TopArtists = 0,
        [FieldOption(Label = "Top Albums")]
        TopAlbums = 1,
        [FieldOption(Label = "Top Tracks")]
        TopTracks = 2
    }
}