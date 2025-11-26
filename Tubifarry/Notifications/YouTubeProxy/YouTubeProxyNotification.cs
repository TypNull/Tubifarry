using FluentValidation.Results;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.ThingiProvider;

namespace Tubifarry.Notifications.YouTubeProxy
{
    public class YouTubeProxyNotification : NotificationBase<YouTubeProxySettings>
    {
        public override string Name => "YouTube Proxy";

        public override string Link => "";

        public override ProviderMessage Message => new("YouTube Proxy configures SOCKS5 proxy settings for HTTP requests. Configure your shadowsocks proxy details to route traffic through the specified server.", ProviderMessageType.Info);

        public override ValidationResult Test() => new();

        public override void OnGrab(GrabMessage message)
        { }
    }
}