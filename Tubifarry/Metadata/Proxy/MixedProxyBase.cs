using NLog;
using NzbDrone.Core.ThingiProvider;
using System.Collections.Concurrent;
using System.Reflection;

namespace Tubifarry.Metadata.Proxy
{
    public abstract class MixedProxyBase<TSettings> : ProxyBase<TSettings>, IMixedProxy
        where TSettings : class, IProviderConfig, new()
    {
        protected readonly Lazy<IProxyService> ProxyService;
        protected readonly Logger _logger;
        private readonly InternalProxyWrapper _proxyWrapper;

        protected MixedProxyBase(Lazy<IProxyService> proxyService, Logger logger)
        {
            ProxyService = proxyService;
            _logger = logger;
            _proxyWrapper = new InternalProxyWrapper(proxyService);
        }

        protected T InvokeProxyMethod<T>(IProxy proxy, string methodName, params object[] args) =>
            _proxyWrapper.InvokeProxyMethodDirect<T>(proxy, methodName, args);

        protected void InvokeProxyMethodVoid(IProxy proxy, string methodName, params object[] args) =>
            _proxyWrapper.InvokeProxyMethodVoidDirect(proxy, methodName, args);

        protected T InvokeProxyMethod<T>(Type interfaceType, string methodName, params object[] args) =>
            _proxyWrapper.InvokeProxyMethod<T>(interfaceType, methodName, args);

        protected void InvokeProxyMethodVoid(Type interfaceType, string methodName, params object[] args) =>
                _proxyWrapper.InvokeProxyMethodVoid(interfaceType, methodName, args);

        private class InternalProxyWrapper(Lazy<IProxyService> proxyService) : ProxyWrapperBase(proxyService)
        {
            private readonly ConcurrentDictionary<string, MethodInfo> _directMethodCache = new();

            public T InvokeProxyMethodDirect<T>(IProxy proxy, string methodName, params object[] args)
            {
                MethodInfo method = GetOrCreateCachedMethod(_directMethodCache, proxy.GetType(), methodName, args);
                return InvokeWithUnwrapping<T>(method, proxy, args);
            }

            public void InvokeProxyMethodVoidDirect(IProxy proxy, string methodName, params object[] args)
            {
                MethodInfo method = GetOrCreateCachedMethod(_directMethodCache, proxy.GetType(), methodName, args);
                InvokeWithUnwrapping(method, proxy, args);
            }

            public new T InvokeProxyMethod<T>(Type interfaceType, string methodName, params object[] args) =>
                base.InvokeProxyMethod<T>(interfaceType, methodName, args);

            public new void InvokeProxyMethodVoid(Type interfaceType, string methodName, params object[] args) =>
                base.InvokeProxyMethodVoid(interfaceType, methodName, args);
        }
    }
}