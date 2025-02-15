using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using Tubifarry.ImportLists.WantedList;

namespace Tubifarry.Metadata.Proxy.DiscogsProxy
{
    public class DiscogsMetadataProxySettingsValidator : AbstractValidator<DiscogsMetadataProxySettings>
    {
        public DiscogsMetadataProxySettingsValidator()
        {
            RuleFor(x => x.AuthToken).NotEmpty().WithMessage("A Discogs API key is required.");

            // Validate PageNumber must be greater than 0
            RuleFor(x => x.PageNumber)
                .GreaterThan(0)
                .WithMessage("Page number must be greater than 0.");

            // Validate PageSize must be greater than 0
            RuleFor(x => x.PageSize)
                .GreaterThan(0)
                .WithMessage("Page size must be greater than 0.");

            // When using Permanent cache, require a valid CacheDirectory.
            RuleFor(x => x.CacheDirectory)
                .Must((settings, path) => (settings.CacheType != SearchSniperCacheType.Permanent) || (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)))
                .WithMessage("A valid Cache Directory is required for Permanent caching.");
        }
    }

    public class DiscogsMetadataProxySettings : IProviderConfig
    {
        private static readonly DiscogsMetadataProxySettingsValidator Validator = new();

        [FieldDefinition(1, Label = "Token", Type = FieldType.Textbox, Privacy = PrivacyLevel.ApiKey, HelpText = "Your Discogs personal access token", Placeholder = "Enter your API key")]
        public string AuthToken { get; set; } = string.Empty;

        [FieldDefinition(2, Label = "Page Number", Type = FieldType.Number, HelpText = "Page number for pagination", Placeholder = "1")]
        public int PageNumber { get; set; } = 1;

        [FieldDefinition(3, Label = "Page Size", Type = FieldType.Number, HelpText = "Page size for pagination", Placeholder = "5")]
        public int PageSize { get; set; } = 5;

        [FieldDefinition(4, Label = "Cache Type", Type = FieldType.Select, SelectOptions = typeof(SearchSniperCacheType), HelpText = "Select Memory (non-permanent) or Permanent caching")]
        public SearchSniperCacheType CacheType { get; set; } = SearchSniperCacheType.Memory;

        [FieldDefinition(5, Label = "Cache Directory", Type = FieldType.Path, HelpText = "Directory to store cached data (only used for Permanent caching)")]
        public string CacheDirectory { get; set; } = string.Empty;

        public string BaseUrl => "https://api.discogs.com";
        public DiscogsMetadataProxySettings() => Instance = this;
        public static DiscogsMetadataProxySettings? Instance { get; private set; }
        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}