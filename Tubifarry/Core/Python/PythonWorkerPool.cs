using NLog;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;

namespace Tubifarry.Core.Python
{
    public sealed record PythonWorkerSpec(string Key, string Name, PythonEnvironment Environment, string HandlerScript, IReadOnlyDictionary<string, string> Variables);

    public interface IPythonWorkerPool
    {
        Task<JsonNode?> InvokeAsync(PythonWorkerSpec spec, string method, object? parameters, TimeSpan timeout, IProgress<PythonWorkerProgress>? progress = null, CancellationToken token = default);
        void Stop(string key);
        bool IsInUse(string environmentDirectory);
        bool ReleaseIdle(string environmentDirectory);
    }

    public sealed class PythonWorkerPool : IPythonWorkerPool, IHandle<ApplicationShutdownRequested>, IDisposable
    {
        private const string WorkerScript = "tubifarry_worker.py";
        private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);

        private readonly Logger _logger;
        private readonly ConcurrentDictionary<string, PythonWorker> _workers = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _startGates = new(StringComparer.Ordinal);
        private readonly Timer _idleTimer;
        private int _shutdown;

        public PythonWorkerPool(Logger logger)
        {
            _logger = logger;
            _idleTimer = new Timer(_ => StopIdleWorkers(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }

        public async Task<JsonNode?> InvokeAsync(PythonWorkerSpec spec, string method, object? parameters, TimeSpan timeout, IProgress<PythonWorkerProgress>? progress = null, CancellationToken token = default)
        {
            PythonWorker worker = await GetWorkerAsync(spec, token);
            try
            {
                return await worker.InvokeAsync(method, parameters, timeout, progress, token);
            }
            catch (PythonWorkerUnavailableException ex) when (!ex.RequestSent)
            {
                _logger.Debug("Python worker '{0}' was unavailable ({1}), starting a new one", spec.Name, ex.Message);
                Remove(spec.Key, worker);
                worker = await GetWorkerAsync(spec, token);
                return await worker.InvokeAsync(method, parameters, timeout, progress, token);
            }
        }

        public void Stop(string key)
        {
            if (_workers.TryRemove(key, out PythonWorker? worker))
                worker.Dispose();
        }

        public bool IsInUse(string environmentDirectory) =>
            _workers.Values.Any(worker => worker.IsAlive && string.Equals(worker.EnvironmentDirectory, environmentDirectory, StringComparison.Ordinal));

        public bool ReleaseIdle(string environmentDirectory)
        {
            List<PythonWorker> workers = _workers.Values.Where(worker => string.Equals(worker.EnvironmentDirectory, environmentDirectory, StringComparison.Ordinal)).ToList();
            if (workers.Any(worker => worker.IsAlive && worker.IsBusy))
                return false;

            foreach (PythonWorker worker in workers)
                Remove(worker.Key, worker);

            return !IsInUse(environmentDirectory);
        }

        public void Handle(ApplicationShutdownRequested message) => Dispose();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _shutdown, 1) == 1)
                return;

            _idleTimer.Dispose();
            foreach (string key in _workers.Keys.ToList())
                Stop(key);
        }

        private async Task<PythonWorker> GetWorkerAsync(PythonWorkerSpec spec, CancellationToken token)
        {
            ThrowIfShutdown();

            if (_workers.TryGetValue(spec.Key, out PythonWorker? existing) && existing.IsAlive)
                return existing;

            SemaphoreSlim gate = _startGates.GetOrAdd(spec.Key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(token);
            try
            {
                if (_workers.TryGetValue(spec.Key, out existing))
                {
                    if (existing.IsAlive)
                        return existing;

                    Remove(spec.Key, existing);
                }

                string scriptDirectory = Path.Combine(spec.Environment.Directory, "tubifarry");
                string workerScript = WriteScript(scriptDirectory, WorkerScript);
                string handlerScript = WriteScript(scriptDirectory, spec.HandlerScript);

                PythonWorker worker = await PythonWorker.StartAsync(spec.Key, spec.Name, spec.Environment, workerScript, handlerScript, spec.Variables, StartTimeout, _logger, token);

                if (_shutdown == 1)
                {
                    worker.Dispose();
                    ThrowIfShutdown();
                }

                _workers[spec.Key] = worker;
                return worker;
            }
            finally
            {
                gate.Release();
            }
        }

        private void Remove(string key, PythonWorker worker)
        {
            if (_workers.TryRemove(new KeyValuePair<string, PythonWorker>(key, worker)))
                worker.Dispose();
        }

        private void ThrowIfShutdown()
        {
            if (_shutdown == 1)
                throw new PythonWorkerUnavailableException("Lidarr is shutting down.");
        }

        private void StopIdleWorkers()
        {
            try
            {
                DateTime threshold = DateTime.UtcNow - IdleTimeout;
                foreach (PythonWorker worker in _workers.Values.Where(w => !w.IsAlive || (!w.IsBusy && w.LastUsed < threshold)).ToList())
                {
                    _logger.Debug("Stopping idle Python worker '{0}'", worker.Key);
                    Remove(worker.Key, worker);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Stopping idle Python workers failed");
            }
        }

        private static string WriteScript(string directory, string name)
        {
            using Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Tubifarry.Python.{name}")
                ?? throw new InvalidOperationException($"The embedded Python script '{name}' is missing.");
            using MemoryStream buffer = new();
            resource.CopyTo(buffer);
            byte[] content = buffer.ToArray();

            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, name);
            if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(content))
                File.WriteAllBytes(path, content);

            return path;
        }
    }
}
