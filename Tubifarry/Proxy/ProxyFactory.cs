using NLog;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.ThingiProvider;
using Tubifarry.Proxy.MixedProxy;
using Tubifarry.Proxy.SkyHook;

namespace Tubifarry.Proxy
{
    public interface IProxyFactory : IProviderFactory<IProxy, ProxyDefinition>
    {
        IProxy GetActiveProxy();
        TProxy GetProxy<TProxy>() where TProxy : class, IProxy;
        List<IProxy> Enabled();
    }

    public class ProxyFactory : ProviderFactory<IProxy, ProxyDefinition>, IProxyFactory
    {
        private readonly IProxyRepository _providerRepository;
        private readonly Logger _logger;

        public ProxyFactory(IProxyRepository providerRepository, IEnumerable<IProxy> providers, IServiceProvider serviceProvider, IEventAggregator eventAggregator, Logger logger)
            : base(providerRepository, providers, serviceProvider, eventAggregator, logger)
        {
            _providerRepository = providerRepository;
            _logger = logger;
        }

        protected override void InitializeProviders()
        {
            List<ProxyDefinition> definitions = new();

            foreach (IProxy provider in _providers)
                definitions.Add(new()
                {
                    Enable = false,
                    Name = provider.Name,
                    Implementation = provider.GetType().Name,
                    Settings = Activator.CreateInstance(provider.ConfigContract) as IProviderConfig
                });

            List<ProxyDefinition> currentProviders = All();

            List<ProxyDefinition> newProviders = definitions.Where(def => currentProviders.All(c => c.Implementation != def.Implementation)).ToList();

            if (newProviders.Any()) _providerRepository.InsertMany(newProviders.Cast<ProxyDefinition>().ToList());
        }


        public List<IProxy> Enabled() => GetAvailableProviders().Where(n => ((MetadataDefinition)n.Definition).Enable).ToList();

        public IProxy GetActiveProxy()
        {
            List<IProxy> enabledProxies = Enabled();
            if (!enabledProxies.Any())
            {
                return GetProxy<SkyHookIProxy>();
            }

            if (enabledProxies.Count > 0)
            {
                IProxy? mixedProxy = enabledProxies.FirstOrDefault(x => x is IMixedProxy);
                if (mixedProxy == null)
                    return GetProxy<SkyHookIProxy>();
                else
                    return mixedProxy;
            }
            return enabledProxies.First();
        }

        public TProxy GetProxy<TProxy>() where TProxy : class, IProxy => _providers.OfType<TProxy>().First();

    }
}
