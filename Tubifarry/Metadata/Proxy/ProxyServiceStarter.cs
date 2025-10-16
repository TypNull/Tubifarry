using NzbDrone.Common.Messaging;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;

namespace Tubifarry.Metadata.Proxy
{
    public enum ProxyMode
    {
        Public,
        Internal
    }

    public interface IProxy
    {
        public string Name { get; }
    }

    public interface IMixedProxy : IProxy;

    public enum ProxyStatusAction
    {
        Enabled,
        Disabled
    }

    public class ProxyStatusChangedEvent(IProxy proxy, ProxyStatusAction action) : IEvent
    {
        public IProxy Proxy { get; } = proxy;
        public ProxyStatusAction Action { get; } = action;
    }

    public class ProxyServiceStarter : IHandle<ApplicationStartedEvent>
    {
        public static IProxyService? ProxyService { get; private set; }

        public ProxyServiceStarter(IProxyService proxyService) => ProxyService = proxyService;

        public void Handle(ApplicationStartedEvent message) => ProxyService?.InitializeProxies();
    }
}