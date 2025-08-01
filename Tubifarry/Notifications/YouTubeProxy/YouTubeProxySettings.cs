using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace Tubifarry.Notifications.YouTubeProxy
{
    public class YouTubeProxySettingsValidator : AbstractValidator<YouTubeProxySettings>
    {
        public YouTubeProxySettingsValidator()
        {
            RuleFor(c => c.ProxyHost)
                .NotEmpty()
                .WithMessage("Proxy host is required when proxy is enabled");

            RuleFor(c => c.ProxyPort)
                .GreaterThan(0)
                .LessThanOrEqualTo(65535)
                .WithMessage("Proxy port must be between 1 and 65535");

            RuleFor(c => c.Username)
                .NotEmpty()
                .When(c => c.RequireAuthentication)
                .WithMessage("Username is required when authentication is enabled");

            RuleFor(c => c.Password)
                .NotEmpty()
                .When(c => c.RequireAuthentication)
                .WithMessage("Password is required when authentication is enabled");
        }
    }

    public class YouTubeProxySettings : IProviderConfig
    {
        private static readonly YouTubeProxySettingsValidator Validator = new();

        [FieldDefinition(1, Label = "Proxy Host", Type = FieldType.Textbox, HelpText = "The hostname or IP address of your SOCKS5 proxy server.")]
        public string ProxyHost { get; set; } = "";

        [FieldDefinition(2, Label = "Proxy Port", Type = FieldType.Number, HelpText = "The port number of your SOCKS5 proxy server (typically 1080).")]
        public int ProxyPort { get; set; } = 1080;

        [FieldDefinition(3, Label = "Require Authentication", Type = FieldType.Checkbox, HelpText = "Enable if your SOCKS5 proxy requires username and password authentication.")]
        public bool RequireAuthentication { get; set; }

        [FieldDefinition(4, Label = "Username", Type = FieldType.Textbox, HelpText = "Username for proxy authentication (only required if authentication is enabled).")]
        public string Username { get; set; } = "";

        [FieldDefinition(5, Label = "Password", Type = FieldType.Password, HelpText = "Password for proxy authentication (only required if authentication is enabled).")]
        public string Password { get; set; } = "";

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}