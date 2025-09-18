using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Metadata.Proxy.MetadataProvider.Discogs
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
                .Must((settings, path) => settings.RequestCacheType != (int)CacheType.Permanent || !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                .WithMessage("A valid Cache Directory is required for Permanent caching.");

            // Validate the system stability for Memory cache
            RuleFor(x => x.RequestCacheType)
                .Must((type) => type == (int)CacheType.Permanent || Tubifarry.AverageRuntime > TimeSpan.FromDays(4) ||
                           DateTime.UtcNow - Tubifarry.LastStarted > TimeSpan.FromDays(5))
                .When(x => x.RequestCacheType == (int)CacheType.Memory)
                .WithMessage("The system is not detected as stable. Please wait for the system to stabilize or use permanent cache.");

            // Validate the User-Agent
            RuleFor(x => x.UserAgent)
                .Must(x => UserAgentValidator.Instance.IsAllowed(x))
                .WithMessage("The provided User-Agent is not allowed." +
                "Ensure it follows the format 'Name/Version' and avoids terms like: lidarr, bot, crawler or proxy.");
        }
    }

    public class DiscogsMetadataProxySettings : IProviderConfig
    {
        private static readonly DiscogsMetadataProxySettingsValidator Validator = new();

        [FieldDefinition(1, Label = "Token", Type = FieldType.Textbox, Privacy = PrivacyLevel.ApiKey, HelpText = "Your Discogs personal access token", Placeholder = "Enter your API key")]
        public string AuthToken { get; set; } = string.Empty;

        [FieldDefinition(2, Label = "User Agent", Section = MetadataSectionType.Metadata, Type = FieldType.Textbox, HelpText = "Specify a custom User-Agent to identify yourself. A User-Agent helps servers understand the software making the request. Use a unique identifier that includes a name and version. Avoid generic or suspicious-looking User-Agents to prevent blocking.", Placeholder = "Lidarr/1.0.0")]
        public string UserAgent { get; set; } = string.Empty;

        [FieldDefinition(3, Label = "Page Number", Type = FieldType.Number, HelpText = "Page number for pagination", Placeholder = "1")]
        public int PageNumber { get; set; } = 1;

        [FieldDefinition(4, Label = "Page Size", Type = FieldType.Number, HelpText = "Page size for pagination", Placeholder = "5")]
        public int PageSize { get; set; } = 5;

        [FieldDefinition(5, Label = "Cache Type", Type = FieldType.Select, SelectOptions = typeof(CacheType), HelpText = "Select Memory (non-permanent) or Permanent caching")]
        public int RequestCacheType { get; set; } = (int)CacheType.Permanent;

        [FieldDefinition(6, Label = "Cache Directory", Type = FieldType.Path, HelpText = "Directory to store cached data (only used for Permanent caching)")]
        public string CacheDirectory { get; set; } = string.Empty;

        public string BaseUrl => "https://api.discogs.com";
        public DiscogsMetadataProxySettings() => Instance = this;
        public static DiscogsMetadataProxySettings? Instance { get; private set; }
        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}