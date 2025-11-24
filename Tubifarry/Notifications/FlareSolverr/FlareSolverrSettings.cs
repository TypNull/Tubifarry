using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace Tubifarry.Notifications.FlareSolverr
{
    public class FlareSolverrSettingsValidator : AbstractValidator<FlareSolverrSettings>
    {
        public FlareSolverrSettingsValidator()
        {
            RuleFor(c => c.ApiUrl)
                .NotEmpty()
                .WithMessage("FlareSolverr API URL is required")
                .Must(url => Uri.IsWellFormedUriString(url, UriKind.Absolute))
                .WithMessage("FlareSolverr API URL must be a valid absolute URL");

            RuleFor(c => c.MaxTimeout)
                .GreaterThan(0)
                .LessThanOrEqualTo(300000)
                .WithMessage("Max timeout must be between 1 and 300000 milliseconds");

            RuleFor(c => c.CacheDurationMinutes)
                .GreaterThanOrEqualTo(1)
                .LessThanOrEqualTo(1440)
                .WithMessage("Cache duration must be between 1min and 24 hours");

            RuleFor(c => c.MaxRetries)
                .GreaterThan(0)
                .LessThanOrEqualTo(10)
                .WithMessage("Max retries must be between 1 and 10");
        }
    }

    public class FlareSolverrSettings : IProviderConfig
    {
        private static readonly FlareSolverrSettingsValidator Validator = new();

        [FieldDefinition(1, Label = "FlareSolverr API URL", Type = FieldType.Textbox, HelpText = "The URL of your FlareSolverr instance (e.g., http://localhost:8191/).")]
        public string ApiUrl { get; set; } = "http://localhost:8191/";

        [FieldDefinition(2, Label = "Max Timeout (ms)", Type = FieldType.Number, HelpText = "Maximum timeout in milliseconds for solving challenges (default: 60000 = 60 seconds).")]
        public int MaxTimeout { get; set; } = 60000;

        [FieldDefinition(3, Label = "Cache Duration (minutes)", Type = FieldType.Number, HelpText = "How long to cache solved challenges in memory (default: 30 minutes).", Advanced = true)]
        public int CacheDurationMinutes { get; set; } = 30;

        [FieldDefinition(4, Label = "Max Retries", Type = FieldType.Number, HelpText = "Maximum number of times to retry solving a challenge (default: 3).", Advanced = true)]
        public int MaxRetries { get; set; } = 3;

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}
