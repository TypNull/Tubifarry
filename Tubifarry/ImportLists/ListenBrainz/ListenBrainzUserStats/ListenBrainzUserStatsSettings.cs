using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Validation;

namespace Tubifarry.ImportLists.ListenBrainz.ListenBrainzUserStats
{
    public class ListenBrainzUserStatsSettingsValidator : AbstractValidator<ListenBrainzUserStatsSettings>
    {
        public ListenBrainzUserStatsSettingsValidator()
        {
            RuleFor(c => c.UserName)
                .NotEmpty()
                .WithMessage("ListenBrainz username is required");

            RuleFor(c => c.Count)
                .GreaterThan(0)
                .LessThanOrEqualTo(100)
                .WithMessage("Count must be between 1 and 100");

            RuleFor(c => c.RefreshInterval)
                .GreaterThanOrEqualTo(1)
                .WithMessage("Refresh interval must be at least 1 day");
        }
    }

    public class ListenBrainzUserStatsSettings : IImportListSettings
    {
        private static readonly ListenBrainzUserStatsSettingsValidator Validator = new();

        public ListenBrainzUserStatsSettings()
        {
            BaseUrl = "https://api.listenbrainz.org";
            RefreshInterval = 7;
            Count = 25;
            Range = (int)ListenBrainzTimeRange.AllTime;
            StatType = (int)ListenBrainzStatType.Artists;
        }

        public string BaseUrl { get; set; }

        [FieldDefinition(0, Label = "ListenBrainz Username", HelpText = "The ListenBrainz username to fetch statistics from", Placeholder = "username")]
        public string UserName { get; set; } = string.Empty;

        [FieldDefinition(1, Label = "User Token", Type = FieldType.Password, HelpText = "Optional ListenBrainz user token for authenticated requests (higher rate limits)", Advanced = true)]
        public string UserToken { get; set; } = string.Empty;

        [FieldDefinition(2, Label = "Statistic Type", Type = FieldType.Select, SelectOptions = typeof(ListenBrainzStatType), HelpText = "Type of statistics to fetch")]
        public int StatType { get; set; }

        [FieldDefinition(3, Label = "Time Range", Type = FieldType.Select, SelectOptions = typeof(ListenBrainzTimeRange), HelpText = "Time period for statistics")]
        public int Range { get; set; }

        [FieldDefinition(4, Label = "Count", Type = FieldType.Number, HelpText = "Number of items to fetch (1-100)")]
        public int Count { get; set; }

        [FieldDefinition(5, Label = "Refresh Interval", Type = FieldType.Textbox, HelpText = "Interval between refreshes in days", Unit = "days", Advanced = true)]
        public double RefreshInterval { get; set; }

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }

    public enum ListenBrainzStatType
    {
        [FieldOption(Label = "Top Artists")]
        Artists = 0,
        [FieldOption(Label = "Top Releases")]
        Releases = 1,
        [FieldOption(Label = "Top Release Groups")]
        ReleaseGroups = 2
    }

    public enum ListenBrainzTimeRange
    {
        [FieldOption(Label = "This Week")]
        ThisWeek = 0,
        [FieldOption(Label = "This Month")]
        ThisMonth = 1,
        [FieldOption(Label = "This Year")]
        ThisYear = 2,
        [FieldOption(Label = "All Time")]
        AllTime = 3
    }
}