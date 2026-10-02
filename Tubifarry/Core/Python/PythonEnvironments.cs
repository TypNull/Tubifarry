using NLog;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Core.Python
{
    public sealed record PythonEnvironmentSpec(string Name, string PythonVersion, IReadOnlyList<string> Packages, string? RootDirectory = null, IReadOnlyList<string>? ImportModules = null);

    public sealed record PythonEnvironment(string Name, string Directory, string Python);

    public sealed class PythonEnvironmentLease(PythonEnvironment environment, Action release) : IDisposable
    {
        private Action? _release = release;

        public PythonEnvironment Environment { get; } = environment;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    public interface IPythonEnvironments
    {
        Task<PythonEnvironmentLease> AcquireAsync(PythonEnvironmentSpec spec, CancellationToken token = default);
    }

    public sealed class PythonEnvironments(IUvInstallation uvInstallation, IPythonWorkerPool workerPool, Logger logger) : IPythonEnvironments
    {
        public const string DefaultPythonVersion = "3.13";

        private const string ReadyMarker = ".tubifarry-ready";
        private static readonly TimeSpan PythonInstallTimeout = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan VenvTimeout = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan PackageTimeout = TimeSpan.FromMinutes(20);
        private static readonly TimeSpan ImportCheckTimeout = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan RefreshAge = TimeSpan.FromDays(7);
        private static readonly TimeSpan RefreshRetryDelay = TimeSpan.FromHours(6);

        private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _leases = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, DateTime> _refreshAttempts = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _refreshing = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _refreshed = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _activeStaging = new(StringComparer.Ordinal);

        public async Task<PythonEnvironmentLease> AcquireAsync(PythonEnvironmentSpec spec, CancellationToken token = default)
        {
            string root = string.IsNullOrWhiteSpace(spec.RootDirectory) ? uvInstallation.DefaultRootDirectory : spec.RootDirectory;
            List<string> packages = NormalizePackages(spec.Packages);
            string key = $"{spec.Name}-{Hash(spec.PythonVersion + "\n" + string.Join('\n', packages))}";
            string envsDirectory = Path.Combine(root, "envs");
            string directory = Path.Combine(envsDirectory, key);

            SemaphoreSlim gate = _gates.GetOrAdd(spec.Name, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(token);
            try
            {
                PythonEnvironment environment = new(spec.Name, directory, GetPython(directory));
                if (IsReady(environment))
                {
                    TryPublishRefresh(environment);
                    if (NeedsRefresh(directory) && !_refreshed.ContainsKey(directory) && _refreshing.TryAdd(directory, 0))
                        _ = Task.Run(() => RefreshAsync(spec, root, key, environment));
                    return Lease(environment);
                }

                logger.Info("Preparing Python {0} environment '{1}' with {2}", spec.PythonVersion, spec.Name, packages.Count == 0 ? "no packages" : string.Join(' ', packages));
                await BuildAsync(spec, root, key, directory, token);
                RemoveStale(envsDirectory, spec.Name, directory);
                logger.Info("Python environment '{0}' is ready at {1}", spec.Name, directory);
                return Lease(environment);
            }
            finally
            {
                gate.Release();
            }
        }

        private static bool IsReady(PythonEnvironment environment) =>
            File.Exists(Path.Combine(environment.Directory, ReadyMarker)) && File.Exists(environment.Python);

        private bool NeedsRefresh(string directory) =>
            DateTime.UtcNow - File.GetLastWriteTimeUtc(Path.Combine(directory, ReadyMarker)) > RefreshAge &&
            DateTime.UtcNow - _refreshAttempts.GetValueOrDefault(directory) > RefreshRetryDelay;

        private async Task RefreshAsync(PythonEnvironmentSpec spec, string root, string key, PythonEnvironment environment)
        {
            try
            {
                _refreshAttempts[environment.Directory] = DateTime.UtcNow;
                logger.Debug("Refreshing Python environment '{0}'", spec.Name);

                string staging = await BuildStagingAsync(spec, root, key, CancellationToken.None);
                if (_refreshed.TryGetValue(environment.Directory, out string? previous))
                    DiscardStaging(previous);
                _refreshed[environment.Directory] = staging;

                SemaphoreSlim gate = _gates.GetOrAdd(spec.Name, _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync();
                try
                {
                    TryPublishRefresh(environment);
                }
                finally
                {
                    gate.Release();
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Refreshing Python environment '{0}' failed, the current environment stays in use", spec.Name);
            }
            finally
            {
                _refreshing.TryRemove(environment.Directory, out _);
            }
        }

        private void TryPublishRefresh(PythonEnvironment environment)
        {
            if (!_refreshed.TryGetValue(environment.Directory, out string? staging))
                return;

            if (IsLeased(environment.Directory) || !workerPool.ReleaseIdle(environment.Directory))
            {
                logger.Debug("Python environment '{0}' is in use, its refresh is applied later", environment.Name);
                return;
            }

            _refreshed.TryRemove(environment.Directory, out _);
            try
            {
                FileSystemHelper.ReplaceDirectory(staging, environment.Directory);
                logger.Info("Python environment '{0}' was refreshed with the newest allowed package versions", environment.Name);
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Applying the refreshed Python environment '{0}' failed, the current environment stays in use", environment.Name);
            }
            finally
            {
                DiscardStaging(staging);
            }
        }

        private void DiscardStaging(string staging)
        {
            FileSystemHelper.TryDeleteDirectory(staging);
            _activeStaging.TryRemove(staging, out _);
        }

        private async Task BuildAsync(PythonEnvironmentSpec spec, string root, string key, string directory, CancellationToken token)
        {
            string staging = await BuildStagingAsync(spec, root, key, token);
            try
            {
                FileSystemHelper.ReplaceDirectory(staging, directory);
            }
            finally
            {
                DiscardStaging(staging);
            }
        }

        private async Task<string> BuildStagingAsync(PythonEnvironmentSpec spec, string root, string key, CancellationToken token)
        {
            List<string> packages = NormalizePackages(spec.Packages);
            string uv = await uvInstallation.EnsureAsync(root, token);
            IReadOnlyDictionary<string, string> uvEnvironment = CreateUvEnvironment(root);
            string staging = Path.Combine(root, "envs", $".staging-{key}-{Guid.NewGuid():N}");
            _activeStaging[staging] = 0;

            try
            {
                await RunUvAsync(uv, ["python", "install", "--no-bin", "--no-registry", spec.PythonVersion], uvEnvironment, PythonInstallTimeout, token);
                await RunUvAsync(uv, ["venv", "--relocatable", "--python", spec.PythonVersion, staging], uvEnvironment, VenvTimeout, token);

                if (packages.Count > 0)
                    await RunUvAsync(uv, ["pip", "install", "--python", GetPython(staging), .. packages], uvEnvironment, PackageTimeout, token);

                if (spec.ImportModules is { Count: > 0 } modules)
                {
                    ProcessResult check = await ProcessRunner.RunAsync(GetPython(staging), ["-I", "-c", $"import {string.Join(", ", modules)}"], ImportCheckTimeout, token);
                    if (!check.Succeeded)
                        throw new InvalidOperationException($"The new '{spec.Name}' environment cannot import {string.Join(", ", modules)}: {LastLines(check.StandardError, 3)}");
                }

                await File.WriteAllTextAsync(Path.Combine(staging, ReadyMarker), string.Join('\n', packages), token);
                return staging;
            }
            catch
            {
                DiscardStaging(staging);
                throw;
            }
        }

        public static IReadOnlyDictionary<string, string> CreateUvEnvironment(string root) => new Dictionary<string, string>
        {
            ["UV_PYTHON_INSTALL_DIR"] = Path.Combine(root, "interpreters"),
            ["UV_CACHE_DIR"] = Path.Combine(root, "cache"),
            ["UV_PYTHON_PREFERENCE"] = "only-managed",
            ["UV_NO_CONFIG"] = "1",
            ["UV_SYSTEM_CERTS"] = "1",
            ["UV_NO_PROGRESS"] = "1",
            ["NO_COLOR"] = "1"
        };

        private PythonEnvironmentLease Lease(PythonEnvironment environment)
        {
            lock (_leases)
                _leases[environment.Directory] = _leases.GetValueOrDefault(environment.Directory) + 1;

            return new PythonEnvironmentLease(environment, () =>
            {
                lock (_leases)
                {
                    if (_leases[environment.Directory] > 1)
                        _leases[environment.Directory]--;
                    else
                        _leases.Remove(environment.Directory);
                }
            });
        }

        private bool IsLeased(string directory)
        {
            lock (_leases)
                return _leases.ContainsKey(directory);
        }

        private async Task RunUvAsync(string uv, IEnumerable<string> arguments, IReadOnlyDictionary<string, string> environment, TimeSpan timeout, CancellationToken token)
        {
            List<string> argumentList = arguments.ToList();
            ProcessResult result = await ProcessRunner.RunAsync(uv, argumentList, timeout, token, environment);

            string output = string.Join('\n', new[] { result.StandardOutput, result.StandardError }.Where(text => !string.IsNullOrWhiteSpace(text)).Select(text => text.Trim()));
            if (output.Length > 0)
                logger.Debug("uv {0}:\n{1}", argumentList[0], output);

            if (!result.Succeeded)
                throw new InvalidOperationException($"uv {string.Join(' ', argumentList.Take(2))} failed (exit code {result.ExitCode}): {LastLines(result.StandardError, 5)}");
        }

        private void RemoveStale(string envsDirectory, string name, string keep)
        {
            foreach (string directory in Directory.EnumerateDirectories(envsDirectory))
            {
                string folder = Path.GetFileName(directory);
                bool sameName = folder.Length == name.Length + 11 && folder.StartsWith(name + "-", StringComparison.Ordinal);
                bool leftover = folder.StartsWith(".staging-", StringComparison.Ordinal) || folder.Contains(".old-", StringComparison.Ordinal);

                if (string.Equals(directory, keep, StringComparison.Ordinal) || !(sameName || leftover) || _activeStaging.ContainsKey(directory) || IsLeased(directory) || workerPool.IsInUse(directory))
                    continue;

                FileSystemHelper.TryDeleteFile(Path.Combine(directory, ReadyMarker));
                if (!FileSystemHelper.TryDeleteDirectory(directory))
                    logger.Debug("Could not remove old Python environment {0}, it may still be in use", directory);
            }
        }

        private static List<string> NormalizePackages(IReadOnlyList<string> packages) =>
            packages.Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();

        private static string GetPython(string directory) => OperatingSystem.IsWindows()
            ? Path.Combine(directory, "Scripts", "python.exe")
            : Path.Combine(directory, "bin", "python");

        private static string Hash(string value) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..10].ToLowerInvariant();

        private static string LastLines(string text, int count) =>
            string.Join(' ', text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(count));
    }
}
