using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Metadata.Proxy.MetadataProvider.Lastfm
{
    public class LastfmMetadataProxySettingsValidator : AbstractValidator<LastfmMetadataProxySettings>
    {
        public LastfmMetadataProxySettingsValidator()
        {
            RuleFor(x => x.ApiKey)
                .NotEmpty()
                .WithMessage("A Last.fm API key is required.");

            // Validate PageNumber must be greater than 0
            RuleFor(x => x.PageNumber)
                .GreaterThan(0)
                .WithMessage("Page number must be greater than 0.");

            // Validate PageSize must be greater than 0
            RuleFor(x => x.PageSize)
                .GreaterThan(0)
                .WithMessage("Page size must be greater than 0.");

            // Validate the system stability for Memory cache
            RuleFor(x => x.RequestCacheType)
                .Must((type) => type == (int)CacheType.Permanent || Tubifarry.AverageRuntime > TimeSpan.FromDays(4) ||
                           DateTime.UtcNow - Tubifarry.LastStarted > TimeSpan.FromDays(5))
                .When(x => x.RequestCacheType == (int)CacheType.Memory)
                .WithMessage("The system is not detected as stable. Please wait for the system to stabilize or use permanent cache.");

            // When using Permanent cache, require a valid CacheDirectory
            RuleFor(x => x.CacheDirectory)
                .Must((settings, path) => settings.RequestCacheType != (int)CacheType.Permanent || !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                .WithMessage("A valid Cache Directory is required for Permanent caching.");

            // Validate the system stability for Memory cache
            RuleFor(x => x.RequestCacheType)
                .Must((type) => type == (int)CacheType.Permanent || Tubifarry.AverageRuntime > TimeSpan.FromDays(4) ||
                           DateTime.UtcNow - Tubifarry.LastStarted > TimeSpan.FromDays(5))
                .When(x => x.RequestCacheType == (int)CacheType.Memory)
                .WithMessage("The system is not detected as stable. Please wait for the system to stabilize or use permanent cache.");

            // Validate that Warn is checked 
            RuleFor(x => x.UseAtOwnRisk)
                .Equal(true)
                .WithMessage("You must acknowledge that this feature is in alpha state by checking the 'Warning' box.");
        }
    }

    public class LastfmMetadataProxySettings : IProviderConfig
    {
        private static readonly LastfmMetadataProxySettingsValidator Validator = new();

        [FieldDefinition(1, Label = "API Key", Type = FieldType.Textbox, Privacy = PrivacyLevel.ApiKey, HelpText = "Your Last.fm API key", Placeholder = "Enter your API key")]
        public string ApiKey { get; set; } = string.Empty;

        [FieldDefinition(2, Label = "User Agent", Section = MetadataSectionType.Metadata, Type = FieldType.Textbox, HelpText = "Specify a custom User-Agent to identify yourself. A User-Agent helps servers understand the software making the request. Use a unique identifier that includes a name and version. Avoid generic or suspicious-looking User-Agents to prevent blocking.", Placeholder = "Lidarr/1.0.0")]
        public string UserAgent { get; set; } = string.Empty;

        [FieldDefinition(3, Label = "Page Number", Type = FieldType.Number, HelpText = "Page number for pagination", Placeholder = "1")]
        public int PageNumber { get; set; } = 1;

        [FieldDefinition(4, Label = "Page Size", Type = FieldType.Number, HelpText = "Page size for pagination", Placeholder = "30")]
        public int PageSize { get; set; } = 3;

        [FieldDefinition(5, Label = "Cache Type", Type = FieldType.Select, SelectOptions = typeof(CacheType), HelpText = "Select Memory (non-permanent) or Permanent caching")]
        public int RequestCacheType { get; set; } = (int)CacheType.Permanent;

        [FieldDefinition(6, Label = "Cache Directory", Type = FieldType.Path, HelpText = "Directory to store cached data (only used for Permanent caching)")]
        public string CacheDirectory { get; set; } = string.Empty;

        [FieldDefinition(7, Label = "Warning", Type = FieldType.Checkbox, HelpText = "Use at your own risk this is not ready and is not in beta but in alpha state")]
        public bool UseAtOwnRisk { get; set; }

        public LastfmMetadataProxySettings() => Instance = this;
        public static LastfmMetadataProxySettings? Instance { get; private set; }
        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}