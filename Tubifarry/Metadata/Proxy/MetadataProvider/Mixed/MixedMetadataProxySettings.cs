using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using Tubifarry.Metadata.Proxy.MetadataProvider.SkyHook;

namespace Tubifarry.Metadata.Proxy.MetadataProvider.Mixed
{
    public class MixedMetadataProxySettingsValidator : AbstractValidator<MixedMetadataProxySettings>
    {
        public MixedMetadataProxySettingsValidator()
        {
            // Validate string values (must be integers between 0 and 50).
            RuleForEach(x => x.Priotities)
                .Must(kvp => int.TryParse(kvp.Value, out int intValue) && intValue >= 0 && intValue <= 50)
                .WithMessage("The priotity for a Proxy must be a number between 0 and 50.");

            // Validate ArtistQueryTimeoutSeconds.
            RuleFor(x => x.ArtistQueryTimeoutSeconds)
                .GreaterThan(0)
                .WithMessage("Artist Query Timeout must be greater than 0 seconds.");

            // Validate MaxThreshold (must not exceed 25).
            RuleFor(x => x.MaxThreshold)
                .InclusiveBetween(1, 25)
                .WithMessage("Max Threshold must be between 1 and 25.");
        }
    }

    public class MixedMetadataProxySettings : IProviderConfig
    {
        private static readonly MixedMetadataProxySettingsValidator Validator = new();

        private readonly IEnumerable<KeyValuePair<string, string>> _priotities;
        public static MixedMetadataProxySettings? Instance { get; private set; }

        public MixedMetadataProxySettings()
        {
            _priotities = ProxyServiceStarter.ProxyService?.ActiveProxies?
                .Where(x => x is ISupportMetadataMixing)
                .Where(x => x.GetProxyMode() != ProxyMode.Internal)
                .Select(x => new KeyValuePair<string, string>(x.Name, x is SkyHookMetadataProxy ? "0" : "50"))
                .ToList() ?? Enumerable.Empty<KeyValuePair<string, string>>();
            _customConversion = _priotities.ToList();
            Instance = this;
            ArtistQueryTimeoutSeconds = 30;
            MaxThreshold = 15;
        }

        [FieldDefinition(1, Label = "Priority Rules", Type = FieldType.KeyValueList, Section = MetadataSectionType.Metadata, HelpText = "Define priority rules for proxies. Values must be between 0 and 50.")]
        public IEnumerable<KeyValuePair<string, string>> Priotities
        {
            get => _customConversion;
            set
            {
                if (value != null)
                {
                    Dictionary<string, string> customDict = value.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
                    _customConversion = [.. _priotities
                        .Select(kvp => new KeyValuePair<string, string>(kvp.Key, customDict.TryGetValue(kvp.Key, out string? customValue) ? customValue : kvp.Value))
                        .OrderBy(x => x.Value)];
                }
            }
        }

        private IEnumerable<KeyValuePair<string, string>> _customConversion;

        [FieldDefinition(2, Label = "Maximal usable threshold", Section = MetadataSectionType.Metadata, Type = FieldType.Number, HelpText = "The maximum threshold added to a lower priority proxy to still use for populating data.", Placeholder = "15")]
        public int MaxThreshold { get; set; }

        [FieldDefinition(3, Label = "Dynamic threshold", Section = MetadataSectionType.Metadata, Type = FieldType.Checkbox, HelpText = "Generate a dynamic threshold based on old results or use a static one.")]
        public bool DynamicThresholdMode { get; set; }

        [FieldDefinition(4, Label = "Storing Path", Section = MetadataSectionType.Metadata, Type = FieldType.Path, HelpText = "Path to store the dynamic threshold for usage after restarts.")]
        public string WeightsPath { get; set; } = string.Empty;

        [FieldDefinition(5, Label = "Artist Query Timeout", Unit = "Seconds", Section = MetadataSectionType.Metadata, Type = FieldType.Number, HelpText = "Timeout for artist queries when previous albums exist.", Placeholder = "30")]
        public int ArtistQueryTimeoutSeconds { get; set; }

        [FieldDefinition(6, Label = "Multi-Source Population", Section = MetadataSectionType.Metadata, Type = FieldType.Checkbox, HelpText = "Enable queries to multiple metadata providers when populating artist information. Uses fallback strategy when previous album data exists.")]
        public bool PopulateWithMultipleProxies { get; set; } = true;

        public bool TryFindArtist { get; internal set; }

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}