using System.Reflection;

namespace Tubifarry.Metadata.Proxy
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class ProxyAttribute(ProxyMode mode) : Attribute
    {
        public ProxyMode Mode { get; } = mode;
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class ProxyForAttribute(Type originalInterface, int priority = 0) : Attribute
    {
        public Type OriginalInterface { get; } = originalInterface;
        public int Priority { get; } = priority;
    }

    public static class ProxyAttributeExtensions
    {
        public static void ValidateProxyImplementation(this Type proxyType)
        {
            IEnumerable<(Type OriginalInterface, int Priority)> declaredInterfaces = proxyType.GetDeclaredInterfaces();
            if (!declaredInterfaces.Any()) return;

            MethodInfo[] publicMethods = proxyType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            List<string> missingMethods = declaredInterfaces
                .SelectMany(interfaceInfo => GetMissingMethodsForInterface(interfaceInfo.OriginalInterface, publicMethods))
                .ToList();

            if (missingMethods.Count != 0)
                throw new InvalidOperationException($"Proxy class {proxyType.Name} is missing required methods: {string.Join(", ", missingMethods)}");
        }

        public static IEnumerable<(Type OriginalInterface, int Priority)> GetDeclaredInterfaces(this Type proxyType) =>
            proxyType.GetCustomAttributes<ProxyForAttribute>()
                     .Select(attr => (attr.OriginalInterface, attr.Priority));

        public static ProxyMode GetProxyMode(this IProxy proxy) =>
            proxy.GetType().GetCustomAttribute<ProxyAttribute>()?.Mode ?? ProxyMode.Public;

        public static bool IsProxyForInterface(this Type proxyType, Type originalInterface) =>
            proxyType.GetDeclaredInterfaces().Any(interfaceInfo => interfaceInfo.OriginalInterface == originalInterface);

        public static int GetPriorityForInterface(this Type proxyType, Type originalInterface) =>
            proxyType.GetDeclaredInterfaces()
                     .FirstOrDefault(interfaceInfo => interfaceInfo.OriginalInterface == originalInterface)
                     .Priority;

        public static bool HasMatchingMethod(this Type proxyType, MethodInfo interfaceMethod)
        {
            MethodInfo[] publicInstanceMethods = proxyType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
            return publicInstanceMethods.Any(method => IsMethodMatch(method, interfaceMethod));
        }

        private static IEnumerable<string> GetMissingMethodsForInterface(Type originalInterface, MethodInfo[] cachedProxyMethods) =>
            originalInterface.GetMethods()
                            .Where(interfaceMethod => !cachedProxyMethods.Any(proxyMethod => IsMethodMatch(proxyMethod, interfaceMethod)))
                            .Select(interfaceMethod => $"{originalInterface.Name}.{GetMethodSignature(interfaceMethod)}");

        private static bool IsMethodMatch(MethodInfo proxyMethod, MethodInfo interfaceMethod) =>
            proxyMethod.Name == interfaceMethod.Name &&
            (proxyMethod.IsGenericMethodDefinition || interfaceMethod.IsGenericMethodDefinition
                ? IsGenericMethodMatch(proxyMethod, interfaceMethod)
                : proxyMethod.ReturnType == interfaceMethod.ReturnType && AreParametersMatching(proxyMethod, interfaceMethod));

        private static bool IsGenericMethodMatch(MethodInfo proxyMethod, MethodInfo interfaceMethod)
        {
            if (proxyMethod.IsGenericMethodDefinition != interfaceMethod.IsGenericMethodDefinition)
                return false;

            if (proxyMethod.IsGenericMethodDefinition && interfaceMethod.IsGenericMethodDefinition)
            {
                Type[] proxyGenericArgs = proxyMethod.GetGenericArguments();
                Type[] interfaceGenericArgs = interfaceMethod.GetGenericArguments();

                return proxyGenericArgs.Length == interfaceGenericArgs.Length &&
                       AreGenericParametersMatching(proxyMethod, interfaceMethod) &&
                       AreGenericTypesCompatible(proxyMethod.ReturnType, interfaceMethod.ReturnType);
            }

            return proxyMethod.ReturnType == interfaceMethod.ReturnType &&
                   AreParametersMatching(proxyMethod, interfaceMethod);
        }

        private static bool AreGenericParametersMatching(MethodInfo proxyMethod, MethodInfo interfaceMethod)
        {
            ParameterInfo[] proxyParams = proxyMethod.GetParameters();
            ParameterInfo[] interfaceParams = interfaceMethod.GetParameters();

            return proxyParams.Length == interfaceParams.Length &&
                   proxyParams.Zip(interfaceParams, (proxyParam, interfaceParam) => AreGenericTypesCompatible(proxyParam.ParameterType, interfaceParam.ParameterType))
                              .All(isMatch => isMatch);
        }

        private static bool AreGenericTypesCompatible(Type proxyType, Type interfaceType)
        {
            // Exact type match
            if (proxyType == interfaceType)
                return true;

            // Both are generic parameters, check if they're in the same position
            if (proxyType.IsGenericParameter && interfaceType.IsGenericParameter)
                return proxyType.GenericParameterPosition == interfaceType.GenericParameterPosition;

            // Both are generic types, check definition and arguments recursively
            if (proxyType.IsGenericType && interfaceType.IsGenericType)
            {
                if (proxyType.GetGenericTypeDefinition() != interfaceType.GetGenericTypeDefinition())
                    return false;

                Type[] proxyArgs = proxyType.GetGenericArguments();
                Type[] interfaceArgs = interfaceType.GetGenericArguments();

                return proxyArgs.Length == interfaceArgs.Length &&
                       proxyArgs.Zip(interfaceArgs, (proxyArg, interfaceArg) => AreGenericTypesCompatible(proxyArg, interfaceArg))
                               .All(isMatch => isMatch);
            }

            // Fallback: check assignability for non-generic parameter types
            if (proxyType.IsGenericParameter == interfaceType.IsGenericParameter)
                return proxyType.IsAssignableFrom(interfaceType) || interfaceType.IsAssignableFrom(proxyType);

            return false;
        }

        private static bool AreParametersMatching(MethodInfo proxyMethod, MethodInfo interfaceMethod)
        {
            ParameterInfo[] proxyParams = proxyMethod.GetParameters();
            ParameterInfo[] interfaceParams = interfaceMethod.GetParameters();

            return proxyParams.Length == interfaceParams.Length &&
                   proxyParams.Zip(interfaceParams, (proxyParam, interfaceParam) => proxyParam.ParameterType == interfaceParam.ParameterType)
                              .All(isMatch => isMatch);
        }

        private static string GetMethodSignature(MethodInfo method)
        {
            IEnumerable<string> parameters = method.GetParameters().Select(param => $"{param.ParameterType.Name} {param.Name}");
            string genericParams = method.IsGenericMethodDefinition
                ? $"<{string.Join(", ", method.GetGenericArguments().Select(type => type.Name))}>" : "";

            return $"{method.ReturnType.Name} {method.Name}{genericParams}({string.Join(", ", parameters)})";
        }
    }
}