using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using Tubifarry.ImportLists.WantedList;

namespace Tubifarry.Metadata.Proxy.Deezer
{
    public class DeezerMetadataProxySettingsValidator : AbstractValidator<DeezerMetadataProxySettings>
    {
        public DeezerMetadataProxySettingsValidator()
        {
            // Validate PageNumber must be greater than 0
            RuleFor(x => x.PageNumber)
                .GreaterThan(0)
                .WithMessage("Page number must be greater than 0.");

            // Validate PageSize must be greater than 0
            RuleFor(x => x.PageSize)
                .GreaterThan(0)
                .WithMessage("Page size must be greater than 0.");

            // When using Permanent cache, require a valid CacheDirectory
            RuleFor(x => x.CacheDirectory)
                .Must((settings, path) => (settings.CacheType != CacheType.Permanent) || (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)))
                .WithMessage("A valid Cache Directory is required for Permanent caching.");
        }
    }

    public class DeezerMetadataProxySettings : IProviderConfig
    {
        private static readonly DeezerMetadataProxySettingsValidator Validator = new();

        [FieldDefinition(2, Label = "Page Number", Type = FieldType.Number, HelpText = "Page number for pagination", Placeholder = "1")]
        public int PageNumber { get; set; } = 1;

        [FieldDefinition(3, Label = "Page Size", Type = FieldType.Number, HelpText = "Page size for pagination", Placeholder = "10")]
        public int PageSize { get; set; } = 10;

        [FieldDefinition(4, Label = "Cache Type", Type = FieldType.Select, SelectOptions = typeof(CacheType), HelpText = "Select Memory (non-permanent) or Permanent caching")]
        public CacheType CacheType { get; set; } = CacheType.Memory;

        [FieldDefinition(5, Label = "Cache Directory", Type = FieldType.Path, HelpText = "Directory to store cached data (only used for Permanent caching)")]
        public string CacheDirectory { get; set; } = string.Empty;

        public string BaseUrl => "https://api.deezer.com";

        public DeezerMetadataProxySettings() => Instance = this;
        public static DeezerMetadataProxySettings? Instance { get; private set; }

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}