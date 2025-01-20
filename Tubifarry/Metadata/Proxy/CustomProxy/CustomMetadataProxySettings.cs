using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace Tubifarry.Metadata.Proxy.CustomProxy
{
    public class CustomMetadataProxySettingsValidator : AbstractValidator<CustomMetadataProxySettings>
    {
    }

    public class CustomMetadataProxySettings : IProviderConfig
    {
        private static readonly CustomMetadataProxySettingsValidator Validator = new();

        private readonly IEnumerable<KeyValuePair<string, string>> _defaultConversion;

        public CustomMetadataProxySettings()
        { }

        [FieldDefinition(9, Label = "Custom Conversion Rules", Type = FieldType.KeyValueList, Section = MetadataSectionType.Metadata, HelpText = "Specify custom conversion rules in the format. These rules will override the default settings.")]
        public IEnumerable<KeyValuePair<string, string>> CustomConversion { get; set; }

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}