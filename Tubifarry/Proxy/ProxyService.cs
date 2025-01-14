using DryIoc;
using NLog;
using NzbDrone.Core.MetadataSource;

namespace Tubifarry.Proxy
{
    public interface IProxyService
    {
        void CheckProxy();
    }

    public class ProxyService : IProxyService
    {
        private readonly ILogger _logger;
        private readonly IProxyFactory _proxyFactory;
        private readonly IContainer _container;
        private IProxy? _proxy;

        public ProxyService(IProxyFactory proxyFactory, IContainer container, ILogger logger)
        {
            _logger = logger;
            _proxyFactory = proxyFactory;
            _container = container;
            CheckProxy();
        }

        public void CheckProxy()
        {
            IProxy? activeProxy = _proxyFactory.GetActiveProxy();
            if (activeProxy == _proxy)
                return;
            _proxy = activeProxy;

            _logger.Info($"Enabling {activeProxy.GetType().Name} as Proxy");

            Type[] interfaces = activeProxy.GetType().GetInterfaces();
            foreach (Type interfaceType in interfaces)
                _container.Register(interfaceType, activeProxy.GetType(), Reuse.Singleton, null, null, IfAlreadyRegistered.Replace);


            ISearchForNewArtist artistSearchService = _container.Resolve<ISearchForNewArtist>();
            if (artistSearchService.GetType() == activeProxy.GetType())
                _logger.Debug($"Metadata provider updated successfully to {activeProxy.GetType().Name}");
            else
                _logger.Error($"Metadata provider did not update successfully to {activeProxy.GetType().Name}! Please restart Lidarr to update!");

        }
    }
}