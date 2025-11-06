using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Metadata.ScheduledTasks.SearchSniper
{
    public class SearchSniperSettingsValidator : AbstractValidator<SearchSniperTaskSettings>
    {
        public SearchSniperSettingsValidator()
        {
            // Validate RefreshInterval
            RuleFor(c => c.RefreshInterval)
                .GreaterThanOrEqualTo(5)
                .WithMessage("Refresh interval must be at least 5 minutes.");

            // When using Permanent cache, require a valid CacheDirectory
            RuleFor(x => x.CacheDirectory)
                .Must((settings, path) => (settings.RequestCacheType != (int)CacheType.Permanent) || (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)))
                .WithMessage("A valid Cache Directory is required for Permanent caching.");

            // Validate CacheRetentionDays
            RuleFor(c => c.CacheRetentionDays)
                .GreaterThanOrEqualTo(1)
                .WithMessage("Retention time must be at least 1 day.");

            // Validate RandomPicksPerInterval
            RuleFor(c => c.RandomPicksPerInterval)
                .GreaterThanOrEqualTo(1)
                .WithMessage("At least 1 pick per interval is required.");

            // Validate StopWhenQueuedAlbumsReach
            RuleFor(c => c.StopWhenQueued)
                .GreaterThanOrEqualTo(0)
                .WithMessage("Stop queue threshold must be 0 or greater.");

            // Validatw search options
            RuleFor(c => c)
                .Must(settings => settings.SearchMissing || settings.SearchQualityCutoffNotMet || settings.SearchMissingTracks)
                .WithMessage("At least one search option must be enabled.");
        }
    }

    public class SearchSniperTaskSettings : IProviderConfig
    {
        protected static readonly AbstractValidator<SearchSniperTaskSettings> Validator = new SearchSniperSettingsValidator();

        [FieldDefinition(1, Label = "Min Refresh Interval", Type = FieldType.Textbox, Unit = "minutes", Placeholder = "60", HelpText = "The minimum time between searches for random albums.")]
        public int RefreshInterval { get; set; } = 60;

        [FieldDefinition(2, Label = "Cache Directory", Type = FieldType.Path, Placeholder = "/config/cache", HelpText = "The directory where cached data will be stored. Leave empty for Memory cache.")]
        public string CacheDirectory { get; set; } = string.Empty;

        [FieldDefinition(3, Label = "Cache Retention Time", Type = FieldType.Number, Placeholder = "7", HelpText = "The number of days to retain cached data.")]
        public int CacheRetentionDays { get; set; } = 7;

        [FieldDefinition(4, Label = "Picks Per Interval", Type = FieldType.Number, Placeholder = "5", HelpText = "The number of random albums to search for during each refresh interval.")]
        public int RandomPicksPerInterval { get; set; } = 5;

        [FieldDefinition(5, Label = "Pause When Queued", Type = FieldType.Number, Placeholder = "0", HelpText = "Pause searching when the queue reaches this number. Set to 0 to disable.")]
        public int StopWhenQueued { get; set; }

        [FieldDefinition(6, Label = "Pause When Status", Type = FieldType.Select, SelectOptions = typeof(WaitOnType), HelpText = "Select which queue statuses should be counted when checking the 'Pause When Queued' threshold. This determines which types of items in the queue will prevent new searches from being triggered.")]
        public int WaitOn { get; set; } = (int)WaitOnType.QueuedAndDownloading;

        [FieldDefinition(7, Label = "Cache Type", Type = FieldType.Select, SelectOptions = typeof(CacheType), HelpText = "The type of cache to use for storing search results. Memory cache is faster but does not persist after restart. Permanent cache persists on disk but requires a valid directory.")]
        public int RequestCacheType { get; set; } = (int)CacheType.Memory;

        [FieldDefinition(8, Label = "Missing", Type = FieldType.Checkbox, HelpText = "Search for albums that are missing from your library.")]
        public bool SearchMissing { get; set; } = true;

        [FieldDefinition(9, Label = "Missing Tracks", Type = FieldType.Checkbox, HelpText = "Automatically search for albums that have missing tracks in your library.")]
        public bool SearchMissingTracks { get; set; }

        [FieldDefinition(10, Label = "Cutoff Not Met", Type = FieldType.Checkbox, HelpText = "Automatically search for albums where the current quality does not meet the quality cutoff.")]
        public bool SearchQualityCutoffNotMet { get; set; }

        public string BaseUrl { get; set; } = string.Empty;

        public SearchSniperTaskSettings() => Instance = this;
        public static SearchSniperTaskSettings? Instance { get; private set; }

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }

    public enum WaitOnType
    {
        [FieldOption(Label = "Queued Only", Hint = "Count only items waiting to start downloading")]
        Queued = 0,

        [FieldOption(Label = "Downloading Only", Hint = "Count only items actively downloading")]
        Downloading = 1,

        [FieldOption(Label = "Warning Only", Hint = "Count only items with warnings")]
        Warning = 2,

        [FieldOption(Label = "Queued + Downloading", Hint = "Count items that are queued or actively downloading")]
        QueuedAndDownloading = 3,

        [FieldOption(Label = "All Active Items", Hint = "Count all non-completed items")]
        All = 4
    }

    public class SearchSniperCommand : Command
    {
        public override bool SendUpdatesToClient => true;

        public override bool UpdateScheduledTask => true;

        public override string CompletionMessage => _completionMessage ?? "Search Sniper completed";
        private string? _completionMessage;
        public void SetCompletionMessage(string message) => _completionMessage = message;
    }
}