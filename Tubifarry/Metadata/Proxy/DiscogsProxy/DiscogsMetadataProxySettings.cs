using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using ParkSquare.Discogs;

namespace Tubifarry.Metadata.Proxy.DiscogsProxy
{
    public class DiscogsMetadataProxySettingsValidator : AbstractValidator<DiscogsMetadataProxySettings>
    {
    }

    public class DiscogsMetadataProxySettings : IProviderConfig, IClientConfig
    {
        private static readonly DiscogsMetadataProxySettingsValidator Validator = new();

        [FieldDefinition(1, Label = "Token", Type = FieldType.Textbox, Privacy = PrivacyLevel.ApiKey, HelpText = "Your Discogs personal access token", Placeholder = "Enter your API key")]
        public string AuthToken { get; set; } = string.Empty;

        [FieldDefinition(2, Label = "Page Number", Type = FieldType.Number, HelpText = "Page number for pagination", Placeholder = "1")]
        public int PageNumber { get; set; } = 1;

        [FieldDefinition(3, Label = "Page Size", Type = FieldType.Number, HelpText = "Page size for pagination", Placeholder = "10")]
        public int PageSize { get; set; } = 2;

        public string BaseUrl => "https://api.discogs.com";
        public DiscogsMetadataProxySettings() => Instance = this;
        public static DiscogsMetadataProxySettings? Instance { get; private set; }
        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}