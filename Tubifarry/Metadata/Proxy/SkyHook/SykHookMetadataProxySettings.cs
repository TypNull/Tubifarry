using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace Tubifarry.Metadata.Proxy.SkyHook
{
    public class SykHookMetadataProxySettingsValidator : AbstractValidator<SykHookMetadataProxySettings> { }

    public class SykHookMetadataProxySettings : IProviderConfig
    {
        private static readonly SykHookMetadataProxySettingsValidator Validator = new();

        public SykHookMetadataProxySettings() { }

        [FieldDefinition(99, Label = "Placeholder", Type = FieldType.Textbox, Hidden = HiddenType.Hidden)]
        public string Placeholder { get; set; } = string.Empty;

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}
