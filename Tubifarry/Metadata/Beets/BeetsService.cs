using NLog;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Tubifarry.Core.Python;

namespace Tubifarry.Metadata.Beets
{
    public sealed record BeetsReleaseCandidate(string Id, int Tracks);

    public enum BeetsImportOutcome
    {
        Imported,
        AlreadyPresent,
        NoMatch,
        Missing
    }

    public sealed record BeetsSyncResult(int Items, int Written, int Hardlinked);

    public sealed record BeetsRetagResult(int Retagged, int Files, int Fingerprinted, string? ReleaseId, bool Pinned, bool Hardlinked);

    public interface IBeetsService
    {
        void Enqueue(string albumPath, string albumTitle, BeetsSettings settings);
        Task<string> CheckAsync(BeetsSettings settings, CancellationToken token = default);
        Task<BeetsRetagResult> RetagAsync(BeetsSettings settings, string folder, IReadOnlyList<BeetsReleaseCandidate> releases, CancellationToken token = default);
        Task<BeetsImportOutcome> ImportAlbumAsync(BeetsSettings settings, string albumPath, string albumTitle, bool writeTags = true, CancellationToken token = default);
        Task<IReadOnlySet<string>> GetKnownFoldersAsync(BeetsSettings settings, CancellationToken token = default);
        Task<BeetsSyncResult> SyncAsync(BeetsSettings settings, CancellationToken token = default);
    }

    public sealed class BeetsService(IPythonEnvironments environments, IPythonWorkerPool workers, IUvInstallation uvInstallation, IFpcalcInstallation fpcalcInstallation, Logger logger) : IBeetsService, IHandle<ApplicationShutdownRequested>
    {
        private const string HandlerScript = "beets_handler.py";
        private static readonly TimeSpan CheckTimeout = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan ImportTimeout = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan RetagTimeout = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan KnownFoldersTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan SyncTimeout = TimeSpan.FromHours(12);

        private sealed record BeetsRequest(string AlbumPath, string AlbumTitle, BeetsSettings Settings);

        private readonly Channel<BeetsRequest> _queue = Channel.CreateUnbounded<BeetsRequest>();
        private readonly CancellationTokenSource _shutdown = new();
        private readonly object _workerLock = new();
        private readonly SemaphoreSlim _libraryGate = new(1, 1);

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

        public async Task<BeetsRetagResult> RetagAsync(BeetsSettings settings, string folder, IReadOnlyList<BeetsReleaseCandidate> releases, CancellationToken token = default)
        {
            PythonEnvironmentSpec environmentSpec = settings.ToEnvironmentSpec();
            string? fpcalc = await fpcalcInstallation.TryEnsureAsync(environmentSpec.RootDirectory ?? uvInstallation.DefaultRootDirectory, token);

            using PythonEnvironmentLease lease = await environments.AcquireAsync(environmentSpec, token);
            PythonWorkerSpec spec = CreateSpec(lease.Environment, settings, $"retag-{Guid.NewGuid():N}");
            if (fpcalc != null)
                spec = spec with { Variables = new Dictionary<string, string>(spec.Variables) { ["FPCALC"] = fpcalc } };

            try
            {
                JsonNode? result = await workers.InvokeAsync(spec, "retag", new
                {
                    path = folder,
                    releases = releases.Select(release => new { id = release.Id, tracks = release.Tracks }),
                    library = settings.ResolveDatabasePath(),
                    config = string.IsNullOrWhiteSpace(settings.ConfigPath) ? null : settings.ConfigPath
                }, RetagTimeout, token: token);

                return new BeetsRetagResult(
                    ReadInt(result, "retagged"),
                    ReadInt(result, "files"),
                    ReadInt(result, "fingerprinted"),
                    result?["release"] is JsonValue release && release.TryGetValue(out string? releaseId) ? releaseId : null,
                    ReadBool(result, "pinned"),
                    ReadBool(result, "hardlinked"));
            }
            finally
            {
                workers.Stop(spec.Key);
            }
        }

        private static int ReadInt(JsonNode? node, string name) =>
            node?[name] is JsonValue value && value.TryGetValue(out int number) ? number : 0;

        private static bool ReadBool(JsonNode? node, string name) =>
            node?[name] is JsonValue value && value.TryGetValue(out bool flag) && flag;

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
                        await ImportAlbumAsync(request.Settings, request.AlbumPath, request.AlbumTitle, token: _shutdown.Token);
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

        public async Task<BeetsImportOutcome> ImportAlbumAsync(BeetsSettings settings, string albumPath, string albumTitle, bool writeTags = true, CancellationToken token = default)
        {
            if (!Directory.Exists(albumPath) || !Directory.EnumerateFileSystemEntries(albumPath).Any())
            {
                logger.Debug("Album folder '{0}' is missing or empty, skipping beets", albumPath);
                return BeetsImportOutcome.Missing;
            }

            JsonNode? result = await InvokeLibraryAsync(settings, "import", "import_album", new
            {
                path = albumPath,
                library = settings.ResolveDatabasePath(),
                config = string.IsNullOrWhiteSpace(settings.ConfigPath) ? null : settings.ConfigPath,
                write = writeTags
            }, ImportTimeout, token);
            int items = ReadInt(result, "items");
            int added = ReadInt(result, "added");

            if (added > 0 && ReadBool(result, "hardlinked"))
                logger.Info("beets imported {0} track(s) of '{1}' without writing tags because the files are hardlinked", added, albumTitle);
            else if (added > 0)
                logger.Info("beets imported {0} track(s) of '{1}'", added, albumTitle);
            else if (items > 0)
                logger.Debug("'{0}' is already in the beets library", albumTitle);
            else
                logger.Info("beets skipped '{0}': no confident match", albumTitle);

            return added > 0 ? BeetsImportOutcome.Imported : items > 0 ? BeetsImportOutcome.AlreadyPresent : BeetsImportOutcome.NoMatch;
        }

        public async Task<IReadOnlySet<string>> GetKnownFoldersAsync(BeetsSettings settings, CancellationToken token = default)
        {
            JsonNode? result = await InvokeLibraryAsync(settings, "query", "known_folders", CreateParameters(settings, null), KnownFoldersTimeout, token);
            List<string> folders = result?["folders"]?.Deserialize<List<string>>() ?? [];
            return new HashSet<string>(folders.Select(folder => Path.TrimEndingDirectorySeparator(folder)), OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        }

        public async Task<BeetsSyncResult> SyncAsync(BeetsSettings settings, CancellationToken token = default)
        {
            JsonNode? result = await InvokeLibraryAsync(settings, "sync", "sync", CreateParameters(settings, null), SyncTimeout, token);
            return new BeetsSyncResult(ReadInt(result, "items"), ReadInt(result, "written"), ReadInt(result, "hardlinked"));
        }

        private async Task<JsonNode?> InvokeLibraryAsync(BeetsSettings settings, string purpose, string method, object parameters, TimeSpan timeout, CancellationToken token)
        {
            await _libraryGate.WaitAsync(token);
            try
            {
                using PythonEnvironmentLease lease = await environments.AcquireAsync(settings.ToEnvironmentSpec(), token);
                PythonWorkerSpec spec = CreateSpec(lease.Environment, settings, purpose);
                try
                {
                    return await workers.InvokeAsync(spec, method, parameters, timeout, token: token);
                }
                finally
                {
                    workers.Stop(spec.Key);
                }
            }
            finally
            {
                _libraryGate.Release();
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
