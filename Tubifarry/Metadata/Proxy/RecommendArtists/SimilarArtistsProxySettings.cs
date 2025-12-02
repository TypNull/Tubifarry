using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.ImportLists.LastFm;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Metadata.Proxy.RecommendArtists
{
    public class SimilarArtistsProxySettingsValidator : AbstractValidator<SimilarArtistsProxySettings>
    {
        public SimilarArtistsProxySettingsValidator()
        {
            // Validate that the API key is not empty when enabled
            RuleFor(c => c.ApiKey)
                .NotEmpty()
                .WithMessage("Last.fm API Key is required when Similar Artists feature is enabled");

            // Validate result limit
            RuleFor(c => c.ResultLimit)
                .InclusiveBetween(1, 50)
                .WithMessage("Result limit must be between 1 and 50");

            // Validate the system stability for Memory cache
            RuleFor(x => x.RequestCacheType)
                .Must((type) => type == (int)CacheType.Permanent || Tubifarry.AverageRuntime > TimeSpan.FromDays(1))
                .When(x => x.RequestCacheType == (int)CacheType.Memory)
                .WithMessage("The system is not detected as stable. Please wait for the system to stabilize or use permanent cache.");

            // When using Permanent cache, require a valid CacheDirectory
            RuleFor(x => x.CacheDirectory)
                .Must((settings, path) => settings.RequestCacheType != (int)CacheType.Permanent || (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)))
                .WithMessage("A valid Cache Directory is required for Permanent caching.");

            // Validate the system stability for Memory cache
            RuleFor(x => x.RequestCacheType)
                .Must((type) => type == (int)CacheType.Permanent || Tubifarry.AverageRuntime > TimeSpan.FromDays(1))
                .When(x => x.RequestCacheType == (int)CacheType.Memory)
                .WithMessage("The system is not detected as stable. Please wait for the system to stabilize or use permanent cache.");
        }
    }

    public class SimilarArtistsProxySettings : IProviderConfig
    {
        private static readonly SimilarArtistsProxySettingsValidator Validator = new();

        public SimilarArtistsProxySettings()
        {
            ApiKey = new LastFmUserSettings().ApiKey;
            ResultLimit = 10;
            FetchImages = true;
            Instance = this;
        }

        [FieldDefinition(0, Label = "Last.fm API Key", Type = FieldType.Textbox, Section = MetadataSectionType.Metadata, HelpText = "Your Last.fm API key for fetching similar artists", Privacy = PrivacyLevel.ApiKey)]
        public string ApiKey { get; set; }

        [FieldDefinition(1, Label = "Result Limit", Type = FieldType.Number, Section = MetadataSectionType.Metadata, HelpText = "Maximum number of similar artists to return (1-50). Only artists with MusicBrainz IDs are returned.", Placeholder = "10")]
        public int ResultLimit { get; set; }

        [FieldDefinition(2, Label = "Fetch Images", Type = FieldType.Checkbox, Section = MetadataSectionType.Metadata, HelpText = "Scrape artist images from Last.fm web pages")]
        public bool FetchImages { get; set; }

        [FieldDefinition(3, Label = "Cache Type", Type = FieldType.Select, SelectOptions = typeof(CacheType), HelpText = "Select Memory (non-permanent) or Permanent caching")]
        public int RequestCacheType { get; set; } = (int)CacheType.Permanent;

        [FieldDefinition(4, Label = "Cache Directory", Type = FieldType.Path, HelpText = "Directory to store cached data (only used for Permanent caching)")]
        public string CacheDirectory { get; set; } = string.Empty;

        public static SimilarArtistsProxySettings? Instance { get; private set; }

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}