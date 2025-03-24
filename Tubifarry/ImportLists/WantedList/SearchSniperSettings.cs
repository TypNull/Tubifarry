using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Validation;
using Tubifarry.Core.Utilities;

namespace Tubifarry.ImportLists.WantedList
{
    public class SearchSniperSettingsValidator : AbstractValidator<SearchSniperSettings>
    {
        public SearchSniperSettingsValidator()
        {
            // Validate RefreshInterval
            RuleFor(c => c.RefreshInterval)
                .GreaterThanOrEqualTo(5)
                .WithMessage("Refresh interval must be at least 5 minutes.");

            // Validate CacheDirectory
            // If CacheType is Memory, the directory can be empty.
            RuleFor(c => c.CacheDirectory)
                .Must((settings, path) =>
                {
                    if (settings.SniperCacheType == (int)CacheType.Permanent)
                        return !string.IsNullOrEmpty(path) && Directory.Exists(path);
                    return true;
                })
                .WithMessage("Cache directory must be a valid path when using Permanent cache. For Memory cache, leave this field empty.");

            // Validate CacheRetentionDays
            RuleFor(c => c.CacheRetentionDays)
                .GreaterThanOrEqualTo(1)
                .WithMessage("Retention time must be at least 1 day.");

            // Validate RandomPicksPerInterval
            RuleFor(c => c.RandomPicksPerInterval)
                .GreaterThanOrEqualTo(1)
                .WithMessage("At least 1 pick per interval is required.");
        }
    }

    public class SearchSniperSettings : IImportListSettings
    {
        protected static readonly AbstractValidator<SearchSniperSettings> Validator = new SearchSniperSettingsValidator();

        [FieldDefinition(1, Label = "Min Refresh Interval", Type = FieldType.Textbox, Unit = "minutes", Placeholder = "60", HelpText = "The minimum time between searches for random albums.")]
        public int RefreshInterval { get; set; } = 60;

        [FieldDefinition(2, Label = "Cache Directory", Type = FieldType.Path, Placeholder = "/config/cache", HelpText = "The directory where cached data will be stored. Leave empty for Memory cache.")]
        public string CacheDirectory { get; set; } = string.Empty;

        [FieldDefinition(3, Label = "Cache Retention Time", Type = FieldType.Number, Placeholder = "7", HelpText = "The number of days to retain cached data.")]
        public int CacheRetentionDays { get; set; } = 7;

        [FieldDefinition(4, Label = "Picks Per Interval", Type = FieldType.Number, Placeholder = "5", HelpText = "The number of random albums to search for during each refresh interval.")]
        public int RandomPicksPerInterval { get; set; } = 5;

        [FieldDefinition(5, Label = "Cache Type", Type = FieldType.Select, SelectOptions = typeof(CacheType), HelpText = "The type of cache to use for storing search results. Memory cache is faster but does not persist after restart. Permanent cache persists on disk but requires a valid directory.")]
        public int SniperCacheType { get; set; } = (int)CacheType.Memory;

        public string BaseUrl { get; set; } = string.Empty;
        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}