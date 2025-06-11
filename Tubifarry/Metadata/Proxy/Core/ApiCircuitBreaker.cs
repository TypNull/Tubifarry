using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Tubifarry.Metadata.Proxy.Core
{
    public interface ICircuitBreaker
    {
        bool IsOpen { get; }
        void RecordSuccess();
        void RecordFailure();
        void Reset();
    }

    public class ApiCircuitBreaker : ICircuitBreaker
    {
        private int _failureCount;
        private DateTime _lastFailure = DateTime.MinValue;
        private readonly int _failureThreshold;
        private readonly TimeSpan _resetTimeout;
        private readonly object _lock = new();

        public ApiCircuitBreaker(int failureThreshold = 5, int resetTimeoutMinutes = 5)
        {
            _failureThreshold = failureThreshold;
            _resetTimeout = TimeSpan.FromMinutes(resetTimeoutMinutes);
        }

        public bool IsOpen
        {
            get
            {
                lock (_lock)
                {
                    if (_failureCount >= _failureThreshold)
                    {
                        if (DateTime.UtcNow - _lastFailure > _resetTimeout)
                        {
                            Reset();
                            return false;
                        }
                        return true;
                    }
                    return false;
                }
            }
        }

        public void RecordSuccess()
        {
            lock (_lock) { _failureCount = Math.Max(0, _failureCount - 1); }
        }

        public void RecordFailure()
        {
            lock (_lock)
            {
                _failureCount++;
                _lastFailure = DateTime.UtcNow;
            }
        }

        public void Reset()
        {
            lock (_lock) { _failureCount = 0; }
        }
    }

    public static class CircuitBreakerFactory
    {
        private static readonly ConditionalWeakTable<Type, ICircuitBreaker> _typeBreakers = new();
        private static readonly ConcurrentDictionary<string, WeakReference<ICircuitBreaker>> _namedBreakers = new();

        private static readonly object _cleanupLock = new();
        private static DateTime _lastCleanup = DateTime.UtcNow;
        private static readonly TimeSpan _cleanupInterval = TimeSpan.FromMinutes(15);

        /// <summary>
        /// Gets a circuit breaker for a specific type.
        /// </summary>
        public static ICircuitBreaker GetBreaker<T>() => GetBreaker(typeof(T));

        /// <summary>
        /// Gets a circuit breaker for a specific object.
        /// </summary>
        public static ICircuitBreaker GetBreaker(object obj) => GetBreaker(obj.GetType());

        /// <summary>
        /// Gets a circuit breaker for a specific type.
        /// </summary>
        public static ICircuitBreaker GetBreaker(Type type)
        {
            if (!_typeBreakers.TryGetValue(type, out ICircuitBreaker? breaker))
            {
                breaker = new ApiCircuitBreaker();
                _typeBreakers.Add(type, breaker);
            }
            return breaker;
        }

        /// <summary>
        /// Gets a circuit breaker by name.
        /// </summary>
        public static ICircuitBreaker GetBreaker(string name)
        {
            CleanupIfNeeded();

            if (_namedBreakers.TryGetValue(name, out WeakReference<ICircuitBreaker>? weakRef) && weakRef.TryGetTarget(out ICircuitBreaker? breaker))
                return breaker;

            breaker = new ApiCircuitBreaker();
            _namedBreakers[name] = new WeakReference<ICircuitBreaker>(breaker);
            return breaker;
        }

        /// <summary>
        /// Gets a circuit breaker with custom configuration.
        /// </summary>
        public static ICircuitBreaker GetCustomBreaker<T>(int failureThreshold, int resetTimeoutMinutes)
        {
            Type type = typeof(T);
            if (!_typeBreakers.TryGetValue(type, out ICircuitBreaker? breaker))
            {
                breaker = new ApiCircuitBreaker(failureThreshold, resetTimeoutMinutes);
                _typeBreakers.Add(type, breaker);
            }
            return breaker;
        }

        private static void CleanupIfNeeded()
        {
            lock (_cleanupLock)
            {
                if (DateTime.UtcNow - _lastCleanup > _cleanupInterval)
                {
                    CleanupNamedBreakers();
                    _lastCleanup = DateTime.UtcNow;
                }
            }
        }

        private static void CleanupNamedBreakers()
        {
            foreach (KeyValuePair<string, WeakReference<ICircuitBreaker>> kvp in _namedBreakers)
            {
                if (!kvp.Value.TryGetTarget(out _))
                    _namedBreakers.TryRemove(kvp.Key, out _);
            }
        }
    }
}