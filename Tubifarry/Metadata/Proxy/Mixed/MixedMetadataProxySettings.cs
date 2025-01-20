using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using Tubifarry.Metadata.Proxy.SkyHook;

namespace Tubifarry.Metadata.Proxy.Mixed
{
    public class MixedMetadataProxySettingsValidator : AbstractValidator<MixedMetadataProxySettings>
    {
        public MixedMetadataProxySettingsValidator()
        {
            // Validate proxy names
            RuleFor(x => x.CustomConversion)
                .Must(BeValidCustomConversion)
                .WithMessage("Custom conversion contains invalid proxy names.");

            // Validate string values (must be integers between 0 and 50)
            RuleForEach(x => x.CustomConversion)
                .Must(kvp => int.TryParse(kvp.Value, out int intValue) && intValue >= 0 && intValue <= 50)
                .WithMessage("Value for '{PropertyName}' must be a number between 0 and 50.");
        }

        private bool BeValidCustomConversion(IEnumerable<KeyValuePair<string, string>> customConversion)
        {
            if (customConversion == null)
                return true;

            HashSet<string>? validProxyNames = ProxyServiceStarter.ProxyService?.Proxys?
                .Where(x => !(x is IMixedProxy))
                .Select(x => x.Definition.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (validProxyNames == null)
                return true;

            return customConversion.All(kvp => validProxyNames.Contains(kvp.Key));
        }
    }

    public class MixedMetadataProxySettings : IProviderConfig
    {
        private static readonly MixedMetadataProxySettingsValidator Validator = new();

        private readonly IEnumerable<KeyValuePair<string, string>> _defaultConversion;

        public MixedMetadataProxySettings()
        {
            _defaultConversion = ProxyServiceStarter.ProxyService?.Proxys?
                .Where(x => x is not IMixedProxy)
                .Select(x => new KeyValuePair<string, string>(x.Definition.Name, x is SkyHookMetadataProxy ? "0" : "50"))
                .ToList() ?? Enumerable.Empty<KeyValuePair<string, string>>();
            _customConversion = _defaultConversion.ToList();
        }

        [FieldDefinition(9, Label = "Custom Conversion Rules", Type = FieldType.KeyValueList, Section = MetadataSectionType.Metadata, HelpText = "Specify custom conversion rules in the format. These rules will override the default settings.")]
        public IEnumerable<KeyValuePair<string, string>> CustomConversion
        {
            get => _customConversion;
            set
            {
                if (value != null)
                {
                    Dictionary<string, string> customDict = value.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
                    List<KeyValuePair<string, string>> merged = _defaultConversion
                        .Select(kvp => new KeyValuePair<string, string>(kvp.Key, customDict.TryGetValue(kvp.Key, out string? customValue) ? customValue : kvp.Value))
                        .ToList();
                    _customConversion = merged;
                }
            }
        }

        private IEnumerable<KeyValuePair<string, string>> _customConversion;

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}