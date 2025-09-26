using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Validation;

namespace Tubifarry.Indexers.DABMusic
{
    public class DABMusicIndexerSettingsValidator : AbstractValidator<DABMusicIndexerSettings>
    {
        public DABMusicIndexerSettingsValidator()
        {
            // Validate BaseUrl
            RuleFor(x => x.BaseUrl)
                .NotEmpty().WithMessage("Base URL is required.")
                .Must(url => Uri.IsWellFormedUriString(url, UriKind.Absolute))
                .WithMessage("Base URL must be a valid URL.");

            // Validate SearchLimit
            RuleFor(x => x.SearchLimit)
                .InclusiveBetween(1, 100).WithMessage("Search limit must be between 1 and 100.");

            // Validate RequestTimeout
            RuleFor(x => x.RequestTimeout)
                .InclusiveBetween(10, 300).WithMessage("Request timeout must be between 10 and 300 seconds.");

        }
    }

    public class DABMusicIndexerSettings : IIndexerSettings
    {
        private static readonly DABMusicIndexerSettingsValidator _validator = new();

        public DABMusicIndexerSettings()
        {
            BaseUrl = "https://dabmusic.xyz";
            SearchLimit = 50;
            RequestTimeout = 60;
        }

        [FieldDefinition(0, Label = "Base URL", Type = FieldType.Textbox, HelpText = "URL of the DABMusic API instance", Placeholder = "https://dabmusic.xyz")]
        public string BaseUrl { get; set; }

        [FieldDefinition(1, Label = "Search Limit", Type = FieldType.Number, HelpText = "Maximum number of results to return per search", Hidden = HiddenType.Hidden, Advanced = true)]
        public int SearchLimit { get; set; }

        [FieldDefinition(2, Type = FieldType.Number, Label = "Request Timeout", Unit = "seconds", HelpText = "Timeout for requests to DABMusic API", Advanced = true)]
        public int RequestTimeout { get; set; }

        [FieldDefinition(3, Type = FieldType.Number, Label = "Early Download Limit", Unit = "days", HelpText = "Time before release date Lidarr will download from this indexer, empty is no limit", Advanced = true)]
        public int? EarlyReleaseLimit { get; set; }

        public NzbDroneValidationResult Validate() => new(_validator.Validate(this));
    }
}