using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Validation;

namespace Tubifarry.Indexers.Invidious
{
    public class InvidiousIndexerSettingsValidator : AbstractValidator<InvidiousIndexerSettings>
    {
        public InvidiousIndexerSettingsValidator()
        {
            RuleFor(x => x.BaseUrl)
                .NotEmpty().WithMessage("Base URL is required.")
                .Must(url => Uri.IsWellFormedUriString(url, UriKind.Absolute))
                .WithMessage("Base URL must be a valid URL.");

            RuleFor(x => x.SearchLimit)
                .InclusiveBetween(1, 100).WithMessage("Search limit must be between 1 and 100.");

            RuleFor(x => x.RequestTimeout)
                .InclusiveBetween(10, 300).WithMessage("Request timeout must be between 10 and 300 seconds.");
        }
    }

    public class InvidiousIndexerSettings : IIndexerSettings
    {
        private static readonly InvidiousIndexerSettingsValidator _validator = new();

        public InvidiousIndexerSettings()
        {
            SearchLimit = 20;
            RequestTimeout = 30;
        }

        [FieldDefinition(0, Label = "Base URL", Type = FieldType.Textbox, HelpText = "URL of your Invidious instance", Placeholder = "http://localhost:3000")]
        public string BaseUrl { get; set; } = string.Empty;

        [FieldDefinition(1, Label = "Proxy Videos", Type = FieldType.Checkbox, HelpText = "Proxy video streams through Invidious instead of connecting directly to YouTube")]
        public bool ProxyVideos { get; set; } = true;

        [FieldDefinition(2, Label = "Search Limit", Type = FieldType.Number, HelpText = "Maximum number of results to return per search", Advanced = true)]
        public int SearchLimit { get; set; }

        [FieldDefinition(3, Type = FieldType.Number, Label = "Request Timeout", Unit = "seconds", HelpText = "Timeout for requests to Invidious API", Advanced = true)]
        public int RequestTimeout { get; set; }

        [FieldDefinition(4, Type = FieldType.Number, Label = "Early Download Limit", Unit = "days", HelpText = "Time before release date Lidarr will download from this indexer, empty is no limit", Advanced = true)]
        public int? EarlyReleaseLimit { get; set; }

        public NzbDroneValidationResult Validate() => new(_validator.Validate(this));
    }
}
