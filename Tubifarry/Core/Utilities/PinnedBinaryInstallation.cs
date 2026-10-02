using NLog;
using NzbDrone.Common.Http;
using System.Formats.Tar;
using System.IO.Compression;

namespace Tubifarry.Core.Utilities
{
    public abstract class PinnedBinaryInstallation(IHttpClient httpClient, Logger logger)
    {
        private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(20);

        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Dictionary<string, string> _resolved = new(StringComparer.Ordinal);

        protected Logger Logger { get; } = logger;

        protected abstract string ToolName { get; }
        protected abstract string Version { get; }
        protected abstract string ExecutableBaseName { get; }
        protected abstract IReadOnlyList<string> VersionArguments { get; }

        protected abstract string? GetAssetName();
        protected abstract string GetDownloadUrl(string assetName);

        protected string ExecutableName => OperatingSystem.IsWindows() ? $"{ExecutableBaseName}.exe" : ExecutableBaseName;

        protected async Task<string> EnsureExecutableAsync(string rootDirectory, CancellationToken token)
        {
            await _gate.WaitAsync(token);
            try
            {
                string bundled = Path.Combine(rootDirectory, ExecutableBaseName, Version, ExecutableName);

                if (_resolved.TryGetValue(rootDirectory, out string? cached) && File.Exists(cached))
                    return cached;

                if (await IsUsableAsync(bundled, token))
                    return _resolved[rootDirectory] = bundled;

                string? assetName = GetAssetName();
                if (assetName != null)
                {
                    try
                    {
                        await DownloadAsync(assetName, bundled, token);
                        if (await IsUsableAsync(bundled, token))
                            return _resolved[rootDirectory] = bundled;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Logger.Warn(ex, "Downloading {0} {1} failed, looking for {2} on PATH", ToolName, Version, ExecutableName);
                    }
                }

                string? system = FindOnPath();
                if (system != null && await IsUsableAsync(system, token))
                    return _resolved[rootDirectory] = system;

                throw new InvalidOperationException(assetName == null
                    ? $"No {ToolName} build is published for this platform. Install {ExecutableName} and make it available on PATH."
                    : $"{ToolName} {Version} could not be downloaded and no {ExecutableName} was found on PATH.");
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<bool> IsUsableAsync(string executable, CancellationToken token)
        {
            if (!File.Exists(executable))
                return false;

            try
            {
                ProcessResult result = await ProcessRunner.RunAsync(executable, VersionArguments, VersionTimeout, token);
                if (result.Succeeded)
                    Logger.Trace("Using {0} at {1}", result.StandardOutput.Trim(), executable);
                return result.Succeeded;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.Debug(ex, "{0} at {1} could not be started", ToolName, executable);
                return false;
            }
        }

        private async Task DownloadAsync(string assetName, string target, CancellationToken token)
        {
            string url = GetDownloadUrl(assetName);
            string archivePath = BinaryArchiveHelper.CreateTempArchivePath(url);
            string staging = $"{target}.staging-{Guid.NewGuid():N}";

            try
            {
                Logger.Info("Downloading {0} {1} from {2}", ToolName, Version, url);
                await BinaryArchiveHelper.DownloadAsync(httpClient, url, archivePath);
                token.ThrowIfCancellationRequested();

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                ExtractExecutable(archivePath, assetName, staging);

                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

                File.Move(staging, target, true);
                Logger.Info("{0} {1} installed to {2}", ToolName, Version, target);
            }
            finally
            {
                FileSystemHelper.TryDeleteFile(archivePath);
                FileSystemHelper.TryDeleteFile(staging);
            }
        }

        private void ExtractExecutable(string archivePath, string assetName, string destination)
        {
            if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using ZipArchive zip = ZipFile.OpenRead(archivePath);
                ZipArchiveEntry entry = zip.Entries.FirstOrDefault(e => e.Name.Equals(ExecutableName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"'{assetName}' does not contain {ExecutableName}.");
                entry.ExtractToFile(destination, true);
                return;
            }

            using FileStream file = File.OpenRead(archivePath);
            using GZipStream gzip = new(file, CompressionMode.Decompress);
            using TarReader reader = new(gzip);

            while (reader.GetNextEntry() is TarEntry entry)
            {
                if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile && Path.GetFileName(entry.Name) == ExecutableName)
                {
                    entry.ExtractToFile(destination, true);
                    return;
                }
            }

            throw new InvalidOperationException($"'{assetName}' does not contain {ExecutableName}.");
        }

        private string? FindOnPath() =>
            (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => Path.Combine(directory.Trim().Trim('"'), ExecutableName))
                .FirstOrDefault(File.Exists);
    }
}
