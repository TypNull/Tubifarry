using FluentValidation.Results;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.ThingiProvider;

namespace Tubifarry.Notifications.FlareSolverr
{
    public class FlareSolverrNotification : NotificationBase<FlareSolverrSettings>
    {
        public override string Name => "FlareSolverr";

        public override string Link => "https://github.com/FlareSolverr/FlareSolverr";

        public override ProviderMessage Message => new(
            "FlareSolverr automatically bypasses Cloudflare and DDoS-GUARD protection challenges for HTTP requests. " +
            "Requires a running FlareSolverr instance (Docker recommended). " +
            "Configure the API URL to enable transparent challenge solving.",
            ProviderMessageType.Info);

        public override ValidationResult Test() => new();

        public override void OnGrab(GrabMessage message)
        { }
    }
}