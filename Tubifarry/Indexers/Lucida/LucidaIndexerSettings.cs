using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Validation;

namespace Tubifarry.Indexers.Lucida
{
    public class LucidaIndexerSettingsValidator : AbstractValidator<LucidaIndexerSettings>
    {
        public LucidaIndexerSettingsValidator()
        {
            // Validate BaseUrl
            RuleFor(x => x.BaseUrl)
                .NotEmpty().WithMessage("Base URL is required.")
                .Must(url => Uri.IsWellFormedUriString(url, UriKind.Absolute))
                .WithMessage("Base URL must be a valid URL.");

            // Validate service priorities
            RuleForEach(x => x.ServicePriorities)
                .Must(kvp => int.TryParse(kvp.Value, out int v) && v >= 0 && v <= 50)
                .WithMessage("Priority values must be between 0 and 50.");

            // Validate country codes
            RuleFor(x => x.CountryCode)
                .NotEmpty().WithMessage("Country code is required.")
                .Must(cc => cc
                    .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .All(code => code.Trim().Length == 2))
                .WithMessage("Country code must be a semicolon or comma-separated list of 2-letter country codes.");
        }
    }

    public class LucidaIndexerSettings : IIndexerSettings
    {
        private static readonly LucidaIndexerSettingsValidator _validator = new();
        private static readonly Dictionary<string, string> _defaultPriorities = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Qobuz"] = "0",
            ["Tidal"] = "2",
            ["Deezer"] = "3",
            ["SoundCloud"] = "4",
            ["Amazon Music"] = "5",
            ["Yandex Music"] = "6"
        };

        private List<KeyValuePair<string, string>> _servicePriorities;

        public LucidaIndexerSettings()
        {
            CountryCode = "US";
            RequestTimeout = 60;
            BaseUrl = "https://lucida.to";
            _servicePriorities = _defaultPriorities.ToList();
        }

        [FieldDefinition(0, Label = "Base URL", Type = FieldType.Textbox, HelpText = "URL of the Lucida instance", Placeholder = "https://lucida.to")]
        public string BaseUrl { get; set; }

        [FieldDefinition(1, Label = "Country Code", Type = FieldType.Textbox, HelpText = "Two-letter country code for service regions separated by ;")]
        public string CountryCode { get; set; }

        [FieldDefinition(2, Label = "Service Priorities", Type = FieldType.KeyValueList, HelpText = "Define priority for music services.")]
        public IEnumerable<KeyValuePair<string, string>> ServicePriorities
        {
            get
            {
                Dictionary<string, List<ServiceCountry>>? services = LucidaServiceHelper.HasAvailableServices(BaseUrl) ? LucidaServiceHelper.GetAvailableServices(BaseUrl) : null;
                return _defaultPriorities.Keys.Where(displayName => services == null || (LucidaServiceHelper.GetServiceKey(displayName) is string serviceKey && services.ContainsKey(serviceKey)))
                    .Select(displayName => new KeyValuePair<string, string>(displayName, _servicePriorities.FirstOrDefault(p =>
                            string.Equals(p.Key, displayName, StringComparison.OrdinalIgnoreCase)).Value ?? _defaultPriorities[displayName])).OrderBy(kvp => int.Parse(kvp.Value));
            }
            set
            {
                if (value != null)
                {
                    Dictionary<string, string> custom = value.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
                    _servicePriorities = _defaultPriorities.Keys.Select(displayName => new KeyValuePair<string, string>(displayName,
                            custom.TryGetValue(displayName, out string? v) ? v : _defaultPriorities[displayName])).ToList();
                }
                else
                {
                    _servicePriorities = _defaultPriorities.ToList();
                }
            }
        }

        [FieldDefinition(3, Type = FieldType.Number, Label = "Request Timeout", Unit = "seconds", HelpText = "Timeout for requests to Lucida", Advanced = true)]
        public int RequestTimeout { get; set; }

        [FieldDefinition(4, Type = FieldType.Number, Label = "Early Download Limit", Unit = "days", HelpText = "Time before release date Lidarr will download from this indexer, empty is no limit", Advanced = true)]
        public int? EarlyReleaseLimit { get; set; }

        public NzbDroneValidationResult Validate() => new(_validator.Validate(this));
    }
}
