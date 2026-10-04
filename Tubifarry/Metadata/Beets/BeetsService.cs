using NLog;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Tubifarry.Core.Python;

namespace Tubifarry.Metadata.Beets
{
    public interface IBeetsService
    {
        void Enqueue(string albumPath, string albumTitle, BeetsSettings settings);
        Task<string> CheckAsync(BeetsSettings settings, CancellationToken token = default);
    }

    public sealed class BeetsService(IPythonEnvironments environments, IPythonWorkerPool workers, Logger logger) : IBeetsService, IHandle<ApplicationShutdownRequested>
    {
        private const string HandlerScript = "beets_handler.py";
        private static readonly TimeSpan CheckTimeout = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan ImportTimeout = TimeSpan.FromMinutes(30);

        private sealed record BeetsRequest(string AlbumPath, string AlbumTitle, BeetsSettings Settings);

        private readonly Channel<BeetsRequest> _queue = Channel.CreateUnbounded<BeetsRequest>();
        private readonly CancellationTokenSource _shutdown = new();
        private readonly object _workerLock = new();

        private Task? _worker;

        public void Enqueue(string albumPath, string albumTitle, BeetsSettings settings)
        {
            if (!_queue.Writer.TryWrite(new BeetsRequest(albumPath, albumTitle, settings)))
            {
                logger.Warn("Beets queue is closed, skipping '{0}'", albumTitle);
                return;
            }

            logger.Debug("Queued beets import for '{0}'", albumTitle);

            lock (_workerLock)
                _worker ??= Task.Run(ProcessQueueAsync);
        }

        public async Task<string> CheckAsync(BeetsSettings settings, CancellationToken token = default)
        {
            using PythonEnvironmentLease lease = await environments.AcquireAsync(settings.ToEnvironmentSpec(), token);
            PythonWorkerSpec spec = CreateSpec(lease.Environment, settings, $"check-{Guid.NewGuid():N}");
            try
            {
                JsonNode? result = await workers.InvokeAsync(spec, "check", CreateParameters(settings, null), CheckTimeout, token: token);
                return result?["version"] is JsonValue value && value.TryGetValue(out string? version) ? version : "unknown";
            }
            finally
            {
                workers.Stop(spec.Key);
            }
        }

        public void Handle(ApplicationShutdownRequested message)
        {
            _queue.Writer.TryComplete();
            _shutdown.Cancel();
        }

        private async Task ProcessQueueAsync()
        {
            try
            {
                await foreach (BeetsRequest request in _queue.Reader.ReadAllAsync(_shutdown.Token))
                {
                    try
                    {
                        await ImportAsync(request);
                    }
                    catch (Exception) when (_shutdown.IsCancellationRequested)
                    {
                        logger.Debug("beets import for '{0}' was interrupted by shutdown", request.AlbumTitle);
                        throw new OperationCanceledException(_shutdown.Token);
                    }
                    catch (PythonWorkerException ex)
                    {
                        logger.Error("beets failed for '{0}': {1}\n{2}", request.AlbumTitle, ex.Message, ex.PythonTraceback);
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, "beets failed for '{0}'", request.AlbumTitle);
                    }
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                logger.Debug("Beets queue stopped");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Beets queue stopped unexpectedly");
                lock (_workerLock)
                    _worker = null;
            }
        }

        private async Task ImportAsync(BeetsRequest request)
        {
            if (!Directory.Exists(request.AlbumPath) || !Directory.EnumerateFileSystemEntries(request.AlbumPath).Any())
            {
                logger.Debug("Album folder '{0}' is missing or empty, skipping beets", request.AlbumPath);
                return;
            }

            using PythonEnvironmentLease lease = await environments.AcquireAsync(request.Settings.ToEnvironmentSpec(), _shutdown.Token);
            PythonWorkerSpec spec = CreateSpec(lease.Environment, request.Settings, "import");
            object parameters = CreateParameters(request.Settings, request.AlbumPath);

            logger.Debug("Running beets import for '{0}' in {1}", request.AlbumTitle, request.AlbumPath);
            try
            {
                JsonNode? result = await workers.InvokeAsync(spec, "import_album", parameters, ImportTimeout, token: _shutdown.Token);
                int items = result?["items"] is JsonValue value && value.TryGetValue(out int count) ? count : 0;
                int added = result?["added"] is JsonValue addedValue && addedValue.TryGetValue(out int addedCount) ? addedCount : 0;
                bool hardlinked = result?["hardlinked"] is JsonValue linked && linked.TryGetValue(out bool flag) && flag;

                if (added > 0 && hardlinked)
                    logger.Info("beets imported {0} track(s) of '{1}' without writing tags because the files are hardlinked", added, request.AlbumTitle);
                else if (added > 0)
                    logger.Info("beets imported {0} track(s) of '{1}'", added, request.AlbumTitle);
                else if (items > 0)
                    logger.Debug("'{0}' is already in the beets library", request.AlbumTitle);
                else
                    logger.Info("beets skipped '{0}': no confident match", request.AlbumTitle);
            }
            finally
            {
                workers.Stop(spec.Key);
            }
        }

        private static object CreateParameters(BeetsSettings settings, string? albumPath) => new
        {
            path = albumPath,
            library = settings.ResolveDatabasePath(),
            config = string.IsNullOrWhiteSpace(settings.ConfigPath) ? null : settings.ConfigPath
        };

        private static PythonWorkerSpec CreateSpec(PythonEnvironment environment, BeetsSettings settings, string purpose)
        {
            string databasePath = settings.ResolveDatabasePath();
            string beetsDirectory = Path.GetDirectoryName(databasePath)!;
            Directory.CreateDirectory(beetsDirectory);

            string configStamp = !string.IsNullOrWhiteSpace(settings.ConfigPath) && File.Exists(settings.ConfigPath)
                ? File.GetLastWriteTimeUtc(settings.ConfigPath).Ticks.ToString()
                : string.Empty;

            string key = string.Join('|', "beets", purpose, environment.Directory, databasePath, settings.ConfigPath, configStamp);
            return new PythonWorkerSpec(key, "beets", environment, HandlerScript, new Dictionary<string, string> { ["BEETSDIR"] = beetsDirectory });
        }
    }
}
