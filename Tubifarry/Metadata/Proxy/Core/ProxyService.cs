using DryIoc;
using NLog;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.ThingiProvider.Events;

namespace Tubifarry.Metadata.Proxy.Core
{
    public interface IProxyService
    {
        IEnumerable<IProxy> Proxies { get; }
        IEnumerable<IProxy> ActiveProxies { get; }
        void RegisterProxy(IProxy proxy);
        void UnregisterProxy(IProxy proxy);
        void InitializeProxies();
        IProxy? GetActiveProxyForInterface(Type originalInterfaceType);
        void SetActiveProxy(Type originalInterfaceType, IProxy proxy);
    }

    public class ProxyService : IProxyService, IHandle<ProviderUpdatedEvent<IMetadata>>
    {
        private readonly IMetadataFactory _metadataFactory;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        private readonly Dictionary<Type, List<IProxy>> _interfaceToProxyMap = new();
        private readonly Dictionary<Type, IProxy> _activeProxyForInterface = new();
        private readonly List<IProxy> _activeProxies = new();
        private readonly HashSet<IProxy> _defaultProxies = new();

        public IEnumerable<IProxy> Proxies { get; private set; } = Enumerable.Empty<IProxy>();
        public IEnumerable<IProxy> ActiveProxies => _activeProxies;

        public ProxyService(IMetadataFactory metadataFactory, IEnumerable<IProxy> proxies, IEventAggregator eventAggregator, Logger logger)
        {
            _metadataFactory = metadataFactory;
            _eventAggregator = eventAggregator;
            _logger = logger;
            Proxies = proxies;
        }

        public void RegisterProxy(IProxy proxy)
        {
            IEnumerable<(Type OriginalInterface, int Priority)> declaredInterfaces = proxy.GetType().GetDeclaredInterfaces();
            foreach ((Type originalInterface, int priority) in declaredInterfaces)
                AddProxyToInterfaceMapping(proxy, originalInterface, priority);
            _interfaceToProxyMap.Keys.ToList().ForEach(UpdateActiveProxyForInterface);
        }

        public void UnregisterProxy(IProxy proxy)
        {
            foreach (Type? interfaceType in _interfaceToProxyMap.Keys)
                RemoveProxyFromInterfaceMapping(proxy, interfaceType);
            _defaultProxies.Remove(proxy);
            _logger.Trace($"Unregistered proxy {proxy.Name}");
        }

        public IProxy? GetActiveProxyForInterface(Type originalInterfaceType) =>
            _activeProxyForInterface.GetValueOrDefault(originalInterfaceType) ?? TrySetDefaultProxy(originalInterfaceType);

        public void SetActiveProxy(Type originalInterfaceType, IProxy proxy)
        {
            if (!IsProxyRegisteredForInterface(proxy, originalInterfaceType))
                throw new InvalidOperationException($"Proxy {proxy.Name} is not registered for interface {originalInterfaceType.Name}");

            _activeProxyForInterface[originalInterfaceType] = proxy;
            _logger.Trace($"Set active proxy for {originalInterfaceType.Name} to {proxy.Name}");
        }

        public void Handle(ProviderUpdatedEvent<IMetadata> message)
        {
            if (Proxies.OfType<IProvider>().FirstOrDefault(x => x.Definition?.ImplementationName == message.Definition.ImplementationName) is not IProxy updatedProxy)
                return;

            if (message.Definition.Enable)
                EnableProxy(updatedProxy);
            else
                DisableProxy(updatedProxy);
        }

        public void InitializeProxies()
        {
            _logger.Trace("Initializing proxy system");

            IEnumerable<IProxy> metadataProxies = _metadataFactory.GetAvailableProviders().OfType<IProxy>();
            Proxies = Proxies.Where(p => p is not IMetadata).Concat(metadataProxies).Where(ValidateProxy).DistinctBy(x => x.GetType()).ToArray();
            ActivateEnabledProxies();
            EnsureDefaultProxies();

            _logger.Info($"Initialized proxy system: {_activeProxies.Count} active proxies, {_interfaceToProxyMap.Count} interface mappings");
        }

        private void AddProxyToInterfaceMapping(IProxy proxy, Type originalInterface, int priority)
        {
            _interfaceToProxyMap.TryAdd(originalInterface, new List<IProxy>());

            if (!_interfaceToProxyMap[originalInterface].Contains(proxy))
            {
                _interfaceToProxyMap[originalInterface].Add(proxy);
                _logger.Trace($"Mapped {originalInterface.Name} -> {proxy.Name} (priority: {priority})");
            }
        }

        private void RemoveProxyFromInterfaceMapping(IProxy proxy, Type interfaceType)
        {
            if (!_interfaceToProxyMap.TryGetValue(interfaceType, out List<IProxy>? proxies))
                return;

            proxies.Remove(proxy);

            if (!proxies.Any())
                RemoveInterfaceMapping(interfaceType);
            else if (_activeProxyForInterface.GetValueOrDefault(interfaceType) == proxy)
                SetNewActiveProxyForInterface(interfaceType, proxies);
        }

        private void RemoveInterfaceMapping(Type interfaceType)
        {
            _interfaceToProxyMap.Remove(interfaceType);
            _activeProxyForInterface.Remove(interfaceType);
            _logger.Trace($"Removed all mappings for {interfaceType.Name}");
        }

        private void SetNewActiveProxyForInterface(Type interfaceType, List<IProxy> availableProxies)
        {
            IProxy? newActiveProxy = GetHighestPriorityProxy(availableProxies, interfaceType);
            if (newActiveProxy == null)
                return;
            _activeProxyForInterface[interfaceType] = newActiveProxy;
            _logger.Trace($"Changed active proxy for {interfaceType.Name} to {newActiveProxy.Name}");
        }

        private void UpdateActiveProxyForInterface(Type interfaceType)
        {
            List<IProxy> availableProxies = GetActiveProxiesForInterface(interfaceType);
            IProxy? selectedProxy = SelectOptimalProxy(interfaceType, availableProxies);

            if (selectedProxy != null)
                _activeProxyForInterface[interfaceType] = selectedProxy;
        }

        private List<IProxy> GetActiveProxiesForInterface(Type interfaceType) =>
            _interfaceToProxyMap.GetValueOrDefault(interfaceType, new List<IProxy>())
                .Where(p => _activeProxies.Contains(p))
                .ToList();

        private IProxy? SelectOptimalProxy(Type interfaceType, List<IProxy> availableProxies)
        {
            List<IProxy> nonMixedProxies = availableProxies.Where(p => p is not IMixedProxy).ToList();
            List<IProxy> mixedProxies = availableProxies.OfType<IMixedProxy>().Cast<IProxy>().ToList();

            return nonMixedProxies.Count switch
            {
                0 => SelectDefaultProxy(interfaceType),
                1 => SelectSingletonProxy(interfaceType, nonMixedProxies[0]),
                _ => SelectOrchestratorProxy(interfaceType, nonMixedProxies, mixedProxies)
            };
        }

        private IProxy? SelectDefaultProxy(Type interfaceType)
        {
            IProxy? defaultProxy = GetAllNonMixedProxiesForInterface(interfaceType)
                .OrderByDescending(p => p.GetType().GetPriorityForInterface(interfaceType))
                .FirstOrDefault();

            if (defaultProxy != null)
                _logger.Debug($"No active proxies for {interfaceType.Name}, using default: {defaultProxy.Name}");

            return defaultProxy;
        }

        private IProxy SelectSingletonProxy(Type interfaceType, IProxy proxy)
        {
            _logger.Debug($"Single proxy mode for {interfaceType.Name}: {proxy.Name}");
            return proxy;
        }

        private IProxy? SelectOrchestratorProxy(Type interfaceType, List<IProxy> nonMixedProxies, List<IProxy> mixedProxies)
        {
            IProxy? orchestrator = GetHighestPriorityProxy(mixedProxies, interfaceType);
            if (orchestrator == null)
            {
                List<IProxy> availableOrchestrators = Proxies
                    .Where(p => p is IMixedProxy && p.GetType().IsProxyForInterface(interfaceType))
                    .ToList();
                orchestrator = GetHighestPriorityProxy(availableOrchestrators, interfaceType);
            }

            if (orchestrator != null)
            {
                _logger.Trace($"Multiple proxies for {interfaceType.Name}, using orchestrator: {orchestrator.Name}");
                return orchestrator;
            }

            IProxy? fallbackProxy = GetHighestPriorityProxy(nonMixedProxies, interfaceType);
            _logger.Warn($"Multiple proxies for {interfaceType.Name} but no orchestrator available, using: {fallbackProxy?.Name ?? "(none)"}");
            return fallbackProxy;
        }

        private IEnumerable<IProxy> GetAllNonMixedProxiesForInterface(Type interfaceType) =>
            _interfaceToProxyMap.GetValueOrDefault(interfaceType, new List<IProxy>())
                .Where(p => p is not IMixedProxy);

        private static IProxy? GetHighestPriorityProxy(IEnumerable<IProxy> proxies, Type interfaceType) =>
            proxies.OrderByDescending(p => p.GetType().GetPriorityForInterface(interfaceType)).FirstOrDefault();

        private IProxy? TrySetDefaultProxy(Type originalInterfaceType)
        {
            IProxy? defaultProxy = FindHighestPriorityProxyForInterface(originalInterfaceType);
            if (defaultProxy == null)
            {
                _logger.Warn($"No proxy available for interface {originalInterfaceType.Name}");
                return null;
            }

            _logger.Trace($"Auto-setting default proxy for {originalInterfaceType.Name}: {defaultProxy.Name}");
            EnsureProxyIsActive(defaultProxy);
            _defaultProxies.Add(defaultProxy);
            RegisterProxy(defaultProxy);

            return _activeProxyForInterface.GetValueOrDefault(originalInterfaceType);
        }

        private bool IsProxyRegisteredForInterface(IProxy proxy, Type originalInterfaceType) =>
            _interfaceToProxyMap.ContainsKey(originalInterfaceType) &&
            _interfaceToProxyMap[originalInterfaceType].Contains(proxy);

        private void EnableProxy(IProxy proxy)
        {
            if (_activeProxies.Contains(proxy)) return;

            _activeProxies.Add(proxy);
            RemoveSupersededDefaultProxies(proxy);
            RegisterProxy(proxy);
            _eventAggregator.PublishEvent(new ProxyStatusChangedEvent(proxy, ProxyStatusAction.Enabled));
            _logger.Info($"Enabled proxy: {proxy.Name}");
        }

        private void RemoveSupersededDefaultProxies(IProxy newProxy)
        {
            foreach (Type? interfaceType in newProxy.GetType().GetDeclaredInterfaces().Select(t => t.OriginalInterface))
            {
                IProxy? currentDefault = _defaultProxies.FirstOrDefault(d => d.GetType().IsProxyForInterface(interfaceType));
                if (currentDefault != null)
                {
                    _activeProxies.Remove(currentDefault);
                    _defaultProxies.Remove(currentDefault);
                    _logger.Trace($"Removed superseded default proxy: {currentDefault.Name}");
                }
            }
        }

        private void DisableProxy(IProxy proxy)
        {
            if (!_activeProxies.Contains(proxy))
                return;

            _activeProxies.Remove(proxy);
            _defaultProxies.Remove(proxy);
            UnregisterProxy(proxy);
            _eventAggregator.PublishEvent(new ProxyStatusChangedEvent(proxy, ProxyStatusAction.Disabled));
            _logger.Info($"Disabled proxy: {proxy.Name}");
        }

        private bool ValidateProxy(IProxy proxy)
        {
            try
            {
                proxy.GetType().ValidateProxyImplementation();
                _logger.Trace($"Validated proxy implementation: {proxy.Name}");
                return true;
            }
            catch (InvalidOperationException ex)
            {
                _logger.Warn($"{ex.Message}. Proxy will be excluded from the system.");
                return false;
            }
        }

        private void ActivateEnabledProxies()
        {
            foreach (IProxy proxy in Proxies.Where(x => (x as IProvider)?.Definition?.Enable == true))
            {
                ProxyMode mode = proxy.GetProxyMode();
                int interfaceCount = proxy.GetType().GetDeclaredInterfaces().Count();

                _logger.Trace($"Activating proxy: {proxy.Name} (Mode: {mode}, Interfaces: {interfaceCount})");
                _activeProxies.Add(proxy);
                RegisterProxy(proxy);
            }
        }

        private void EnsureDefaultProxies()
        {
            foreach (Type? originalInterface in GetAllDeclaredInterfaces().Where(i => !_activeProxyForInterface.ContainsKey(i)))
                SetDefaultProxyForInterface(originalInterface);
        }

        private IEnumerable<Type> GetAllDeclaredInterfaces() =>
            Proxies.SelectMany(proxy => proxy.GetType().GetDeclaredInterfaces())
                   .Select(tuple => tuple.OriginalInterface)
                   .Distinct();

        private void SetDefaultProxyForInterface(Type originalInterface)
        {
            IProxy? defaultProxy = FindHighestPriorityProxyForInterface(originalInterface);

            if (defaultProxy != null)
            {
                _logger.Trace($"Setting default proxy for {originalInterface.Name}: {defaultProxy.Name}");
                EnsureProxyIsActive(defaultProxy);
                _defaultProxies.Add(defaultProxy);
                RegisterProxy(defaultProxy);
            }
            else
            {
                _logger.Warn($"No proxy available to handle interface {originalInterface.Name}");
            }
        }

        private void EnsureProxyIsActive(IProxy proxy)
        {
            if (!_activeProxies.Contains(proxy))
                _activeProxies.Add(proxy);
        }

        private IProxy? FindHighestPriorityProxyForInterface(Type originalInterface) =>
            Proxies.Where(proxy => proxy.GetType().IsProxyForInterface(originalInterface))
                   .OrderByDescending(proxy => proxy.GetType().GetPriorityForInterface(originalInterface))
                   .FirstOrDefault();
    }
}