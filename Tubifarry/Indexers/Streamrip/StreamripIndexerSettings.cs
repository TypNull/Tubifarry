using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Validation;

namespace Tubifarry.Indexers.Streamrip
{
    public enum StreamripSource
    {
        [FieldOption(Label = "SoundCloud")]
        SoundCloud = 0,

        [FieldOption(Label = "Qobuz")]
        Qobuz = 1,

        [FieldOption(Label = "Deezer")]
        Deezer = 2
    }

    public enum StreamripQuality
    {
        [FieldOption(Label = "MP3 320")]
        Lossy = 0,

        [FieldOption(Label = "FLAC 16-bit")]
        Lossless = 1,

        [FieldOption(Label = "FLAC 24-bit up to 96 kHz")]
        HiRes = 2,

        [FieldOption(Label = "FLAC 24-bit up to 192 kHz")]
        HiResMax = 3
    }

    public class StreamripIndexerSettingsValidator : AbstractValidator<StreamripIndexerSettings>
    {
        public StreamripIndexerSettingsValidator()
        {
            // Username validation
            RuleFor(x => x.Username)
                .NotEmpty()
                .When(x => x.Source == (int)StreamripSource.Qobuz)
                .WithMessage("Qobuz needs your e-mail address or user ID.");

            // Secret validation
            RuleFor(x => x.Secret)
                .NotEmpty()
                .When(x => x.Source is (int)StreamripSource.Qobuz or (int)StreamripSource.Deezer)
                .WithMessage("Qobuz needs your password or token, Deezer needs your ARL.");

            // Search Limit validation
            RuleFor(x => x.SearchLimit)
                .InclusiveBetween(5, 100);

            // Install Directory validation
            RuleFor(x => x.InstallDirectory)
                .Must(path => string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
                .WithMessage("Install directory must be an absolute path or empty.");
        }
    }

    public class StreamripIndexerSettings : IIndexerSettings
    {
        private static readonly StreamripIndexerSettingsValidator Validator = new();

        [FieldDefinition(0, Label = "Source", Type = FieldType.Select, SelectOptions = typeof(StreamripSource), HelpText = "Streaming service to search and download from. SoundCloud needs no account.")]
        public int Source { get; set; } = (int)StreamripSource.SoundCloud;

        [FieldDefinition(1, Label = "Username", Type = FieldType.Textbox, HelpText = "Qobuz e-mail address, or user ID when using a token. Not used for Deezer and SoundCloud.")]
        public string Username { get; set; } = string.Empty;

        [FieldDefinition(2, Label = "Password / ARL", Type = FieldType.Password, Privacy = PrivacyLevel.Password, HelpText = "Qobuz password or token, or the Deezer ARL cookie.")]
        public string Secret { get; set; } = string.Empty;

        [FieldDefinition(3, Label = "Qobuz Token Login", Type = FieldType.Checkbox, Advanced = true, HelpText = "Treat the username as Qobuz user ID and the password as auth token.")]
        public bool UseToken { get; set; }

        [FieldDefinition(4, Label = "Qobuz App ID", Type = FieldType.Textbox, Advanced = true, HelpText = "Optional. Leave empty to let streamrip find a working app ID.")]
        public string AppId { get; set; } = string.Empty;

        [FieldDefinition(5, Label = "Qobuz App Secret", Type = FieldType.Password, Privacy = PrivacyLevel.ApiKey, Advanced = true, HelpText = "Optional. Needed together with the app ID.")]
        public string AppSecret { get; set; } = string.Empty;

        [FieldDefinition(6, Label = "Quality", Type = FieldType.Select, SelectOptions = typeof(StreamripQuality), HelpText = "Highest quality to download. SoundCloud is always MP3.")]
        public int Quality { get; set; } = (int)StreamripQuality.Lossless;

        [FieldDefinition(7, Label = "Search Limit", Type = FieldType.Number, Advanced = true, HelpText = "Maximum number of albums per search.")]
        public int SearchLimit { get; set; } = 30;

        [FieldDefinition(8, Label = "Install Directory", Type = FieldType.Path, Advanced = true, HelpText = "Folder for uv, the managed Python and the streamrip environment. Leave empty to use the plugin directory.")]
        public string InstallDirectory { get; set; } = string.Empty;

        [FieldDefinition(9, Type = FieldType.Number, Label = "Early Download Limit", Unit = "days", HelpText = "Time before release date Lidarr will download from this indexer, empty is no limit", Advanced = true)]
        public int? EarlyReleaseLimit { get; set; }

        public string BaseUrl { get; set; } = string.Empty;

        public StreamripSource SourceType => (StreamripSource)Source;

        public string SourceName => SourceType.ToString().ToLowerInvariant();

        public int SourceQuality => SourceType switch
        {
            StreamripSource.Qobuz => Quality + 1,
            StreamripSource.Deezer => Quality == (int)StreamripQuality.Lossy ? 1 : 2,
            _ => 0
        };

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}
