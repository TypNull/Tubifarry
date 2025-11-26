using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace Tubifarry.Metadata.Proxy.MetadataProvider.CustomLidarr
{
    public class CustomLidarrMetadataProxySettingsValidator : AbstractValidator<CustomLidarrMetadataProxySettings>
    {
        private static readonly string[] _restrictedDomains = ["musicbrainz.org", "lidarr.audio"];

        public CustomLidarrMetadataProxySettingsValidator()
        {
            // Validate that Warning is checked
            RuleFor(x => x.UseAtOwnRisk)
                .Equal(true)
                .WithMessage("You must acknowledge that this feature could void support by the servarr team by checking the 'Warning' box.");

            // Validate URL format and restrictions
            RuleFor(x => x.MetadataSource)
                .NotEmpty()
                .WithMessage("Metadata Source URL is required.")
                .IsValidUrl()
                .WithMessage("Metadata Source must be a valid HTTP or HTTPS URL.")
                .Must(NotBeRestrictedDomain)
                .WithMessage("Official MusicBrainz and Lidarr API endpoints are not allowed. Please use alternative metadata sources.");
        }

        private static bool NotBeRestrictedDomain(string url) => !_restrictedDomains.Any(domain =>
                url.Contains(domain, StringComparison.InvariantCultureIgnoreCase));
    }

    public class CustomLidarrMetadataProxySettings : IProviderConfig
    {
        private static readonly CustomLidarrMetadataProxySettingsValidator Validator = new();

        [FieldDefinition(0, Label = "Metadata Source", Type = FieldType.Url, Placeholder = "https://api.musicinfo.pro", Section = MetadataSectionType.Metadata, HelpText = "URL to a compatible MusicBrainz API instance.")]
        public string MetadataSource { get; set; } = string.Empty;

        [FieldDefinition(1, Label = "Proxy Spotify", Type = FieldType.Checkbox, HelpText = "Enable if this metadata source can handle Spotify API requests.")]
        public bool CanProxySpotify { get; set; }

        [FieldDefinition(99, Label = "Warning", Type = FieldType.Checkbox, HelpText = "Use at your own risk. This feature could void your Servarr support.")]
        public bool UseAtOwnRisk { get; set; }

        public CustomLidarrMetadataProxySettings() => Instance = this;

        public static CustomLidarrMetadataProxySettings? Instance { get; private set; }

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}