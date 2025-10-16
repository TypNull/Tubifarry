using System.Collections.Concurrent;
using System.Reflection;

namespace Tubifarry.Metadata.Proxy
{
    public abstract class ProxyWrapperBase(Lazy<IProxyService> proxyService)
    {
        protected readonly Lazy<IProxyService> ProxyService = proxyService;
        private readonly ConcurrentDictionary<string, MethodInfo> _methodCache = new();

        protected T InvokeProxyMethod<T>(Type interfaceType, string methodName, params object[] args)
        {
            IProxy activeProxy = GetActiveProxyOrThrow(interfaceType);
            MethodInfo method = GetOrCreateCachedMethod(_methodCache, activeProxy.GetType(), methodName, args);
            object result = method.Invoke(activeProxy, args)!;
            return (T)result;
        }

        protected void InvokeProxyMethodVoid(Type interfaceType, string methodName, params object[] args)
        {
            IProxy activeProxy = GetActiveProxyOrThrow(interfaceType);
            MethodInfo method = GetOrCreateCachedMethod(_methodCache, activeProxy.GetType(), methodName, args);
            method.Invoke(activeProxy, args);
        }

        private IProxy GetActiveProxyOrThrow(Type interfaceType) =>
            ProxyService.Value.GetActiveProxyForInterface(interfaceType) ??
            throw new InvalidOperationException($"No active proxy found for interface {interfaceType.Name}");

        protected static MethodInfo GetOrCreateCachedMethod(ConcurrentDictionary<string, MethodInfo> cache, Type proxyType, string methodName, object[] args)
        {
            string cacheKey = GenerateCacheKey(proxyType, methodName, args);
            return cache.GetOrAdd(cacheKey, _ => FindAndPrepareMethod(proxyType, methodName, args));
        }

        private static MethodInfo FindAndPrepareMethod(Type proxyType, string methodName, object[] args)
        {
            MethodInfo method = FindMatchingMethod(proxyType, methodName, args);

            if (method.IsGenericMethodDefinition)
            {
                Type[] genericArguments = InferGenericArguments(method, args);
                method = method.MakeGenericMethod(genericArguments);
            }

            return method;
        }

        private static MethodInfo FindMatchingMethod(Type proxyType, string methodName, object[] args)
        {
            Type[] parameterTypes = args.Select(arg => arg?.GetType() ?? typeof(object)).ToArray();

            MethodInfo? method = proxyType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance, null, parameterTypes, null);
            if (method?.IsGenericMethodDefinition == false)
                return method;

            IEnumerable<MethodInfo> candidateMethods = proxyType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                           .Where(m => m.Name == methodName && m.GetParameters().Length == args.Length);

            method = candidateMethods.Where(m => !m.IsGenericMethodDefinition)
                                   .FirstOrDefault(m => IsParameterCompatible(m.GetParameters(), args)) ??
                     candidateMethods.FirstOrDefault(m => m.IsGenericMethodDefinition);

            return method ?? throw new InvalidOperationException($"Method {methodName} not found on {proxyType.Name}");
        }

        private static bool IsParameterCompatible(ParameterInfo[] parameters, object[] args)
        {
            for (int i = 0; i < parameters.Length; i++)
            {
                Type paramType = parameters[i].ParameterType;
                Type? argType = args[i]?.GetType();

                if (argType == null)
                {
                    if (paramType.IsValueType && !IsNullable(paramType))
                        return false;
                }
                else if (!paramType.IsAssignableFrom(argType) && !IsGenericTypeCompatible(paramType, argType))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool IsGenericTypeCompatible(Type paramType, Type argType)
        {
            if (paramType.IsGenericParameter)
                return true;

            if (paramType.IsGenericType)
            {
                Type genericDefinition = paramType.GetGenericTypeDefinition();
                return HasGenericBase(argType, genericDefinition);
            }

            return paramType.IsAssignableFrom(argType);
        }

        private static bool HasGenericBase(Type type, Type genericDefinition)
        {
            Type? current = type;
            while (current != null)
            {
                if (current.IsGenericType && current.GetGenericTypeDefinition() == genericDefinition)
                    return true;
                current = current.BaseType;
            }

            return type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == genericDefinition);
        }

        private static Type[] InferGenericArguments(MethodInfo genericMethod, object[] args)
        {
            ParameterInfo[] parameters = genericMethod.GetParameters();
            Type[] genericParameters = genericMethod.GetGenericArguments();
            Type[] inferredTypes = new Type[genericParameters.Length];

            for (int i = 0; i < parameters.Length && i < args.Length; i++)
            {
                if (args[i] != null)
                {
                    InferFromParameter(parameters[i].ParameterType, args[i].GetType(), genericParameters, inferredTypes);
                }
            }

            for (int i = 0; i < inferredTypes.Length; i++)
                inferredTypes[i] ??= GetDefaultTypeForGeneric(genericParameters[i]);

            return inferredTypes;
        }

        private static void InferFromParameter(Type paramType, Type argType, Type[] genericParams, Type[] inferredTypes)
        {
            if (paramType.IsGenericParameter)
            {
                int index = Array.IndexOf(genericParams, paramType);
                if (index >= 0)
                    inferredTypes[index] ??= argType;
                return;
            }

            if (paramType.IsGenericType)
            {
                Type paramGenericDef = paramType.GetGenericTypeDefinition();
                Type? matchingArgType = FindMatchingGenericType(argType, paramGenericDef);

                if (matchingArgType != null)
                {
                    Type[] paramArgs = paramType.GetGenericArguments();
                    Type[] argArgs = matchingArgType.GetGenericArguments();

                    for (int i = 0; i < Math.Min(paramArgs.Length, argArgs.Length); i++)
                    {
                        InferFromParameter(paramArgs[i], argArgs[i], genericParams, inferredTypes);
                    }
                }
            }
        }

        private static Type? FindMatchingGenericType(Type argType, Type genericDefinition)
        {
            if (argType.IsGenericType && argType.GetGenericTypeDefinition() == genericDefinition)
                return argType;

            Type? current = argType.BaseType;
            while (current != null)
            {
                if (current.IsGenericType && current.GetGenericTypeDefinition() == genericDefinition)
                    return current;
                current = current.BaseType;
            }

            return argType.GetInterfaces()
                          .Where(i => i.IsGenericType)
                          .FirstOrDefault(i => i.GetGenericTypeDefinition() == genericDefinition);
        }

        private static Type GetDefaultTypeForGeneric(Type genericParameter)
        {
            Type[] constraints = genericParameter.GetGenericParameterConstraints();
            return constraints.FirstOrDefault(c => !c.IsInterface) ?? constraints.FirstOrDefault() ?? typeof(object);
        }

        private static bool IsNullable(Type type) =>
            type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>);

        private static string GenerateCacheKey(Type proxyType, string methodName, object[] args) =>
            $"{proxyType.FullName}.{methodName}({string.Join(",", args.Select(arg => arg?.GetType().FullName ?? "null"))})";


    }
}