using DryIoc;
using NLog;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.ThingiProvider.Events;
using Tubifarry.Metadata.Proxy.Core;
using Tubifarry.Metadata.Proxy.Mixed;
using Tubifarry.Metadata.Proxy.SkyHook;

namespace Tubifarry.Metadata.Proxy
{
    public enum ProxyStatusAction
    {
        Enabled,
        Disabled
    }

    public class ProxyStatusChangedEvent : IEvent
    {
        public IProxy Proxy { get; }
        public ProxyStatusAction Action { get; }

        public ProxyStatusChangedEvent(IProxy proxy, ProxyStatusAction action)
        {
            Proxy = proxy;
            Action = action;
        }
    }

    public class ProxyServiceStarter : IHandle<ApplicationStartedEvent>
    {
        public static IProxyService? ProxyService { get; private set; }
        public ProxyServiceStarter(IProxyService service) => ProxyService = service;
        public void Handle(ApplicationStartedEvent message) => ProxyService?.CheckProxy();
    }

    public interface IProxyService
    {
        IList<IProxy> Proxys { get; }
        IList<IProxy> ActiveProxys { get; }
        void CheckProxy();
    }

    public class MetadataProxyService : IProxyService, IHandle<ProviderUpdatedEvent<IMetadata>>
    {
        private readonly ILogger _logger;
        private readonly IMetadataFactory _metadataFactory;
        private readonly IContainer _container;
        private readonly List<IProxy> _activeProxys;
        private readonly IEventAggregator _eventAggregator;
        private IProxy[] _proxys;
        private IProxy? _activeProxy;
        public IList<IProxy> Proxys => _proxys;
        public IList<IProxy> ActiveProxys => _activeProxys;

        private readonly Type[] _interfaces = new Type[]
        {
            typeof(IProxyProvideArtistInfo),
            typeof(IProxyProvideAlbumInfo),
            typeof(IProxySearchForNewAlbum),
            typeof(IProxySearchForNewEntity),
            typeof(IProxySearchForNewArtist)
        };

        public MetadataProxyService(IMetadataFactory metadataFactory, IContainer container, Logger logger, IEventAggregator eventAggregator)
        {
            _logger = logger;
            _metadataFactory = metadataFactory;
            _container = container;
            _eventAggregator = eventAggregator;
            _proxys = _metadataFactory.GetAvailableProviders().OfType<IProxy>().ToArray();
            _activeProxys = _proxys.Where(x => x.Definition.Enable).ToList();

            foreach (Type interfaceType in typeof(ProxyForMetadataProxy).GetInterfaces())
                _container.Register(interfaceType, typeof(ProxyForMetadataProxy), Reuse.Singleton, null, null, IfAlreadyRegistered.Replace);
        }

        public void CheckProxy()
        {
            _logger.Debug("Checking active proxies...");

            if (!_proxys.Any())
            {
                _proxys = _metadataFactory.GetAvailableProviders().OfType<IProxy>().ToArray();
                if (!_proxys.Any())
                    return;
            }

            if (!_activeProxys.Any())
            {
                _logger.Trace("No active proxies found. Enabling SkyHookMetadataProxy as default.");
                EnableProxy(_proxys.First(x => x is SkyHookMetadataProxy));
            }
            else if (_activeProxys.Count > 1)
            {
                _logger.Trace("Multiple active proxies found. Prioritizing IMixedProxy if available.");
                foreach (IProxy proxy in _activeProxys)
                    _logger.Trace($"Active Proxy {proxy.Definition.Name}");
                EnableProxy(_activeProxys.FirstOrDefault(x => x is IMixedProxy) ?? _proxys.First(x => x is IMixedProxy));
            }
            else if (_activeProxys[0] is IMixedProxy)
            {
                _logger.Trace("Only one active proxy, but it's an IMixedProxy. Switching to SkyHookMetadataProxy.");
                EnableProxy(_proxys.First(x => x is SkyHookMetadataProxy));
            }
            else
            {
                _logger.Trace("Enabling the only active proxy: {0}.", _activeProxys[0].GetType().Name);
                EnableProxy(_activeProxys[0]);
            }
        }

        private void EnableProxy(IProxy proxy)
        {
            if (proxy == _activeProxy)
                return;

            _logger.Info($"Enabling {proxy.GetType().Name} as the active proxy.");

            foreach (Type interfaceType in _interfaces)
                _container.Register(interfaceType, proxy.GetType(), Reuse.Singleton, null, null, IfAlreadyRegistered.Replace);

            _activeProxy = proxy;
        }

        public void Handle(ProviderUpdatedEvent<IMetadata> message)
        {
            IProxy? updatedProxy = _proxys.FirstOrDefault(x => x.Definition.ImplementationName == message.Definition.ImplementationName);
            if (updatedProxy == null)
                return;

            if (message.Definition.Enable)
            {
                if (!_activeProxys.Contains(updatedProxy))
                {
                    _activeProxys.Add(updatedProxy);
                    _eventAggregator.PublishEvent(new ProxyStatusChangedEvent(updatedProxy, ProxyStatusAction.Enabled));
                }
            }
            else if (_activeProxys.Contains(updatedProxy))
            {
                _activeProxys.Remove(updatedProxy);
                _eventAggregator.PublishEvent(new ProxyStatusChangedEvent(updatedProxy, ProxyStatusAction.Disabled));
            }
            CheckProxy();
        }
    }
}