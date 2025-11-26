using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace Tubifarry.Metadata.Proxy.MetadataProvider.SkyHook
{
    public class SykHookMetadataProxySettingsValidator : AbstractValidator<SykHookMetadataProxySettings>
    { }

    public class SykHookMetadataProxySettings : IProviderConfig
    {
        private static readonly SykHookMetadataProxySettingsValidator Validator = new();

        [FieldDefinition(99, Label = "Placeholder", Type = FieldType.Textbox, Section = MetadataSectionType.Metadata, Hidden = HiddenType.Hidden)]
        public string Placeholder { get; set; } = string.Empty;

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}