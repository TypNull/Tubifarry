using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;

namespace Tubifarry.Indexers.SubSonic;

public class SubSonicIndexerSettingsValidator : AbstractValidator<SubSonicIndexerSettings>
{
    public SubSonicIndexerSettingsValidator()
    {
        // Base URL validation
        RuleFor(x => x.BaseUrl)
            .ValidRootUrl()
            .Must(url => !url.EndsWith('/'))
            .WithMessage("Server URL must not end with a slash ('/').");

        // External URL validation (only if not empty)
        RuleFor(x => x.ExternalUrl)
            .Must(url => string.IsNullOrEmpty(url) || (Uri.IsWellFormedUriString(url, UriKind.Absolute) && !url.EndsWith('/')))
            .WithMessage("External URL must be a valid URL and must not end with a slash ('/').");

        // Username validation
        RuleFor(x => x.Username)
            .NotEmpty()
            .WithMessage("Username is required.");

        // Password validation
        RuleFor(x => x.Password)
            .NotEmpty()
            .WithMessage("Password is required.");

        // Search limit validation
        RuleFor(x => x.SearchLimit)
            .InclusiveBetween(1, 500)
            .WithMessage("Search limit must be between 1 and 500.");

        // Request timeout validation
        RuleFor(x => x.RequestTimeout)
            .InclusiveBetween(10, 300)
            .WithMessage("Request timeout must be between 10 and 300 seconds.");
    }
}

public class SubSonicIndexerSettings : IIndexerSettings
{
    private static readonly SubSonicIndexerSettingsValidator Validator = new();

    [FieldDefinition(0, Label = "Server URL", Type = FieldType.Url, Placeholder = "https://music.example.com", HelpText = "URL of your SubSonic server (internal API URL).")]
    public string BaseUrl { get; set; } = string.Empty;

    [FieldDefinition(1, Label = "External URL", Type = FieldType.Url, Placeholder = "https://subsonic.example.com", HelpText = "External URL for info links and redirects. Leave empty to use Server URL.", Advanced = true)]
    public string? ExternalUrl { get; set; } = string.Empty;

    [FieldDefinition(2, Label = "Username", Type = FieldType.Textbox, HelpText = "Your SubSonic username.")]
    public string Username { get; set; } = string.Empty;

    [FieldDefinition(3, Label = "Password", Type = FieldType.Password, HelpText = "Your SubSonic password.", Privacy = PrivacyLevel.Password)]
    public string Password { get; set; } = string.Empty;

    [FieldDefinition(4, Label = "Use Token Authentication", Type = FieldType.Checkbox, HelpText = "Use secure token-based authentication (API 1.13.0+). Disable for older servers.", Advanced = true)]
    public bool UseTokenAuth { get; set; } = true;

    [FieldDefinition(5, Label = "Search Limit", Type = FieldType.Number, HelpText = "Maximum number of results to return per search.", Hidden = HiddenType.Hidden, Advanced = true)]
    public int SearchLimit { get; set; } = 50;

    [FieldDefinition(6, Type = FieldType.Number, Label = "Request Timeout", Unit = "seconds", HelpText = "Timeout for requests to SubSonic server.", Advanced = true)]
    public int RequestTimeout { get; set; } = 60;

    [FieldDefinition(7, Type = FieldType.Number, Label = "Early Download Limit", Unit = "days", HelpText = "Time before release date Lidarr will download from this indexer, empty is no limit.", Advanced = true)]
    public int? EarlyReleaseLimit { get; set; }

    public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
}
