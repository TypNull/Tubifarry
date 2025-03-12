using System.Collections.Concurrent;

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
        private static readonly ConcurrentDictionary<Type, ICircuitBreaker> _typeBreakers = new();

        private static readonly ConcurrentDictionary<string, ICircuitBreaker> _namedBreakers = new();

        /// <summary>
        /// Gets a circuit breaker for a specific type.
        /// </summary>
        public static ICircuitBreaker GetBreaker<T>() => _typeBreakers.GetOrAdd(typeof(T), _ => new ApiCircuitBreaker());

        /// <summary>
        /// Gets a circuit breaker for a specific object.
        /// </summary>
        public static ICircuitBreaker GetBreaker(object obj) => _typeBreakers.GetOrAdd(obj.GetType(), _ => new ApiCircuitBreaker());

        /// <summary>
        /// Gets a circuit breaker for a specific type.
        /// </summary>
        public static ICircuitBreaker GetBreaker(Type type) => _typeBreakers.GetOrAdd(type, _ => new ApiCircuitBreaker());

        /// <summary>
        /// Gets a circuit breaker by name.
        /// </summary>
        public static ICircuitBreaker GetBreaker(string name) => _namedBreakers.GetOrAdd(name, _ => new ApiCircuitBreaker());

        /// <summary>
        /// Gets a circuit breaker with custom configuration.
        /// </summary>
        public static ICircuitBreaker GetCustomBreaker<T>(int failureThreshold, int resetTimeoutMinutes) => _typeBreakers.GetOrAdd(typeof(T), _ => new ApiCircuitBreaker(failureThreshold, resetTimeoutMinutes));

    }
}
