using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Validation;

namespace Tubifarry.ImportLists.ListenBrainz.ListenBrainzCFRecommendations
{
    public class ListenBrainzCFRecommendationsSettingsValidator : AbstractValidator<ListenBrainzCFRecommendationsSettings>
    {
        public ListenBrainzCFRecommendationsSettingsValidator()
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

    public class ListenBrainzCFRecommendationsSettings : IImportListSettings
    {
        private static readonly ListenBrainzCFRecommendationsSettingsValidator Validator = new();

        public ListenBrainzCFRecommendationsSettings()
        {
            BaseUrl = "https://api.listenbrainz.org";
            RefreshInterval = 7;
            Count = 25;
        }

        public string BaseUrl { get; set; }

        [FieldDefinition(0, Label = "ListenBrainz Username", HelpText = "The ListenBrainz username to fetch recording recommendations for", Placeholder = "username")]
        public string UserName { get; set; } = string.Empty;

        [FieldDefinition(1, Label = "User Token", Type = FieldType.Password, HelpText = "Optional ListenBrainz user token for authenticated requests (higher rate limits)", Advanced = true)]
        public string UserToken { get; set; } = string.Empty;

        [FieldDefinition(2, Label = "Count", Type = FieldType.Number, HelpText = "Number of recording recommendations to fetch (1-100)")]
        public int Count { get; set; }

        [FieldDefinition(3, Label = "Refresh Interval", Type = FieldType.Textbox, HelpText = "Interval between refreshes in days", Unit = "days", Advanced = true)]
        public double RefreshInterval { get; set; }

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}