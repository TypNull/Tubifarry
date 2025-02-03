using DryIoc;
using NLog;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.ThingiProvider.Events;
using Tubifarry.Metadata.Proxy.Mixed;
using Tubifarry.Metadata.Proxy.SkyHook;

namespace Tubifarry.Metadata.Proxy
{
    public class ProxyServiceStarter : IHandle<ApplicationStartingEvent>
    {
        public static IProxyService? ProxyService { get; private set; }
        public ProxyServiceStarter(IProxyService service, Logger logger) { ProxyService = service; }
        public void Handle(ApplicationStartingEvent message) { }
    }

    public interface IProxyService
    {
        public IList<IProxy> Proxys { get; }
        void CheckProxy();
    }

    public class MetadataProxyService : IProxyService, IHandle<ProviderUpdatedEvent<IMetadata>>, IHandle<ProviderAddedEvent<IMetadata>>
    {
        private readonly ILogger _logger;
        private readonly IMetadataFactory _metadataFactory;
        private readonly IContainer _container;
        private readonly IProxy[] _proxys;
        private readonly List<IProxy> _activeProxys;
        private IProxy? _activeProxy;

        public IList<IProxy> Proxys => _proxys;

        private readonly Type[] _interfaces = new Type[]{
        typeof(IProxyProvideArtistInfo),
        typeof(IProxyProvideAlbumInfo),
        typeof(IProxySearchForNewAlbum),
        typeof(IProxySearchForNewEntity),
        typeof(IProxySearchForNewArtist)};

        public MetadataProxyService(IMetadataFactory metadataFactory, IContainer container, Logger logger)
        {
            _logger = logger;
            _metadataFactory = metadataFactory;
            _container = container;
            _proxys = _metadataFactory.GetAvailableProviders().OfType<IProxy>().ToArray();
            _activeProxys = _proxys.Where(x => x.Definition.Enable).ToList();
            foreach (Type interfaceType in typeof(ProxyForMetadataProxy).GetInterfaces())
                _container.Register(interfaceType, typeof(ProxyForMetadataProxy), Reuse.Singleton, null, null, IfAlreadyRegistered.Replace);
            CheckProxy();
        }

        public void CheckProxy()
        {
            if (!_proxys.Any())
                return;
            if (!_activeProxys.Any())
                EnableProxy(_proxys.First(x => x is SkyHookMetadataProxy));
            else if (_activeProxys.Count > 1)
                EnableProxy(_activeProxys.FirstOrDefault(x => x is IMixedProxy) ?? _proxys.First(x => x is IMixedProxy));
            else if (_activeProxys.First() is IMixedProxy)
                EnableProxy(_proxys.First(x => x is SkyHookMetadataProxy));
            else
                EnableProxy(_activeProxys.First());
        }

        private void EnableProxy(IProxy proxy)
        {
            if (proxy == _activeProxy)
                return;

            _logger.Info($"Enabling {proxy.GetType().Name} as Proxy");

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
                _activeProxys.Add(updatedProxy);
            else
                _activeProxys.Remove(updatedProxy);
            CheckProxy();
        }

        public void Handle(ProviderAddedEvent<IMetadata> message) => CheckProxy();
    }
}