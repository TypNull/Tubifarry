using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Core.Rsgain
{
    public sealed record RsgainExecutable(string Path, IReadOnlyDictionary<string, string> Environment);

    public sealed record RsgainResult(int ExitCode, string StandardOutput, string StandardError);

    public interface IRsgainInstallation
    {
        string DefaultInstallDirectory { get; }
        RsgainExecutable? Find(string? installDirectory);
        void Reset();
        Task<RsgainExecutable> InstallAsync(string? installDirectory, CancellationToken token = default);
        Task<RsgainResult> RunAsync(RsgainExecutable executable, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken token = default);
    }

    public sealed class RsgainInstallation(IHttpClient httpClient, IDiskProvider diskProvider, IAppFolderInfo appFolderInfo, Logger logger) : IRsgainInstallation
    {
        private const string PinnedVersion = "3.8";
        private const string ReleaseBaseUrl = "https://github.com/complexlogic/rsgain/releases/download/";
        private static readonly TimeSpan VersionCheckTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan ApkTimeout = TimeSpan.FromMinutes(10);

        private readonly SemaphoreSlim _installGate = new(1, 1);
        private readonly object _cacheLock = new();

        private RsgainExecutable? _cached;
        private string? _cachedFor;

        public string DefaultInstallDirectory =>
            Path.Combine(appFolderInfo.GetPluginPath(), PluginInfo.Author, PluginInfo.Name, "rsgain");

        private static string ExecutableName => OperatingSystem.IsWindows() ? "rsgain.exe" : "rsgain";

        public RsgainExecutable? Find(string? installDirectory)
        {
            string directory = ResolveDirectory(installDirectory);

            lock (_cacheLock)
            {
                if (_cached != null && _cachedFor == directory && File.Exists(_cached.Path))
                    return _cached;
            }

            RsgainExecutable? found = Search(directory);

            lock (_cacheLock)
            {
                _cached = found;
                _cachedFor = directory;
            }

            return found;
        }

        public void Reset()
        {
            lock (_cacheLock)
                _cached = null;
        }

        private RsgainExecutable? Search(string directory)
        {
            foreach (RsgainExecutable candidate in EnumerateCandidates(directory))
            {
                if (!File.Exists(candidate.Path))
                    continue;

                if (IsUsable(candidate))
                {
                    logger.Trace("Using rsgain at {0}", candidate.Path);
                    return candidate;
                }

                logger.Debug("rsgain at {0} exists but could not be executed", candidate.Path);
            }

            return null;
        }

        public async Task<RsgainExecutable> InstallAsync(string? installDirectory, CancellationToken token = default)
        {
            await _installGate.WaitAsync(token);
            try
            {
                string targetDirectory = ResolveDirectory(installDirectory);

                RsgainExecutable? existing = Search(targetDirectory);
                if (existing != null)
                    return existing;

                diskProvider.CreateFolder(targetDirectory);

                if (OperatingSystem.IsLinux() && BinaryArchiveHelper.IsMuslLibc())
                    await InstallWithApkAsync(targetDirectory, token);
                else
                    await DownloadReleaseAsync(GetReleaseArchiveName(), targetDirectory, token);

                Reset();
                RsgainExecutable installed = Find(targetDirectory)
                    ?? throw new InvalidOperationException($"rsgain was installed to '{targetDirectory}' but could not be executed afterwards.");

                logger.Info("rsgain installed to {0}", installed.Path);
                return installed;
            }
            finally
            {
                _installGate.Release();
            }
        }

        public async Task<RsgainResult> RunAsync(RsgainExecutable executable, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken token = default)
        {
            ProcessStartInfo startInfo = new(executable.Path)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);

            foreach (KeyValuePair<string, string> variable in executable.Environment)
                startInfo.Environment[variable.Key] = variable.Value;

            return await RunProcessAsync(startInfo, timeout, token);
        }

        private string ResolveDirectory(string? installDirectory) =>
            string.IsNullOrWhiteSpace(installDirectory) ? DefaultInstallDirectory : installDirectory;

        private IEnumerable<RsgainExecutable> EnumerateCandidates(string installDirectory)
        {
            yield return new RsgainExecutable(Path.Combine(installDirectory, ExecutableName), new Dictionary<string, string>());
            yield return new RsgainExecutable(Path.Combine(installDirectory, "usr", "bin", "rsgain"), GetApkRootEnvironment(installDirectory));

            foreach (string pathEntry in Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? [])
                yield return new RsgainExecutable(Path.Combine(pathEntry, ExecutableName), new Dictionary<string, string>());
        }

        private bool IsUsable(RsgainExecutable executable)
        {
            try
            {
                RsgainResult result = RunAsync(executable, ["--version"], VersionCheckTimeout).GetAwaiter().GetResult();
                return result.ExitCode == 0;
            }
            catch (Exception ex)
            {
                logger.Trace(ex, "Failed to execute rsgain at {0}", executable.Path);
                return false;
            }
        }

        private static Dictionary<string, string> GetApkRootEnvironment(string root)
        {
            string libraryPath = string.Join(':', Path.Combine(root, "usr", "lib"), Path.Combine(root, "lib"));
            string? existing = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");

            return new Dictionary<string, string>
            {
                ["LD_LIBRARY_PATH"] = string.IsNullOrEmpty(existing) ? libraryPath : $"{libraryPath}:{existing}"
            };
        }

        private async Task InstallWithApkAsync(string root, CancellationToken token)
        {
            const string apkUnavailableMessage = "This system uses musl libc but 'apk' is not available. Install rsgain with your package manager so it is available on PATH.";

            RsgainResult version;
            try
            {
                version = await RunProcessAsync(CreateApkStartInfo(["--version"]), VersionCheckTimeout, token);
            }
            catch (Win32Exception ex)
            {
                throw new PlatformNotSupportedException(apkUnavailableMessage, ex);
            }

            if (version.ExitCode != 0)
                throw new PlatformNotSupportedException(apkUnavailableMessage);

            bool supportsUserMode = version.StandardOutput.StartsWith("apk-tools 3", StringComparison.OrdinalIgnoreCase);

            List<string> arguments =
            [
                "add", "rsgain",
                "--root", root,
                "--initdb",
                "--keys-dir", "/etc/apk/keys",
                "--repositories-file", "/etc/apk/repositories",
                "--no-cache"
            ];
            arguments.AddRange(supportsUserMode ? ["--usermode"] : ["--no-chown", "--no-scripts"]);

            logger.Info("Installing rsgain with apk into {0}", root);
            RsgainResult result = await RunProcessAsync(CreateApkStartInfo(arguments), ApkTimeout, token);
            logger.Debug("apk output: {0}{1}", result.StandardOutput, result.StandardError);

            if (!File.Exists(Path.Combine(root, "usr", "bin", "rsgain")))
                throw new InvalidOperationException($"apk could not install rsgain (exit code {result.ExitCode}): {result.StandardError.Trim()}");
        }

        private static ProcessStartInfo CreateApkStartInfo(IEnumerable<string> arguments)
        {
            ProcessStartInfo startInfo = new("apk")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);

            return startInfo;
        }

        private static async Task<RsgainResult> RunProcessAsync(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken token)
        {
            using Process process = new() { StartInfo = startInfo };
            process.Start();

            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(token);
            Task<string> standardError = process.StandardError.ReadToEndAsync(token);

            using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutSource.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(true);
                token.ThrowIfCancellationRequested();
                throw new TimeoutException($"'{Path.GetFileName(startInfo.FileName)}' did not finish within {timeout.TotalMinutes:0.#} minutes.");
            }

            return new RsgainResult(process.ExitCode, await standardOutput, await standardError);
        }

        private static string GetReleaseArchiveName()
        {
            if (OperatingSystem.IsWindows())
                return $"rsgain-{PinnedVersion}-win64.zip";

            if (OperatingSystem.IsMacOS())
                return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                    ? $"rsgain-{PinnedVersion}-macOS-arm64.zip"
                    : $"rsgain-{PinnedVersion}-macOS-x86_64.zip";

            if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
                return $"rsgain-{PinnedVersion}-Linux.tar.xz";

            throw new PlatformNotSupportedException($"No rsgain build is published for {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture}). Install rsgain with your package manager (e.g. 'apt install rsgain') so it is available on PATH.");
        }

        private async Task DownloadReleaseAsync(string archiveName, string targetDirectory, CancellationToken token)
        {
            string url = $"{ReleaseBaseUrl}v{PinnedVersion}/{archiveName}";
            string archivePath = BinaryArchiveHelper.CreateTempArchivePath(url);

            try
            {
                logger.Info("Downloading rsgain from {0}", url);
                await BinaryArchiveHelper.DownloadAsync(httpClient, url, archivePath);
                token.ThrowIfCancellationRequested();

                ExtractArchive(archivePath, targetDirectory);

                string executablePath = Path.Combine(targetDirectory, ExecutableName);
                if (!OperatingSystem.IsWindows() && diskProvider.FileExists(executablePath))
                    diskProvider.SetFilePermissions(executablePath, "755", null!);
            }
            finally
            {
                if (diskProvider.FileExists(archivePath))
                    diskProvider.DeleteFile(archivePath);
            }
        }

        private static void ExtractArchive(string archivePath, string targetDirectory)
        {
            string fullTargetDirectory = Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            int skippedSegments = HasSingleRootFolder(BinaryArchiveHelper.ListFileEntries(archivePath)) ? 1 : 0;

            BinaryArchiveHelper.ExtractEntries(archivePath, entryKey =>
            {
                string[] segments = SplitEntryKey(entryKey);
                if (segments.Length <= skippedSegments)
                    return null;

                string destinationPath = Path.GetFullPath(Path.Combine(fullTargetDirectory, Path.Combine(segments[skippedSegments..])));
                return destinationPath.StartsWith(fullTargetDirectory, StringComparison.Ordinal) ? destinationPath : null;
            });
        }

        private static bool HasSingleRootFolder(List<string> entryKeys)
        {
            List<string[]> entries = entryKeys.Select(SplitEntryKey).Where(segments => segments.Length > 0).ToList();

            return entries.Count > 0
                && entries.All(segments => segments.Length > 1)
                && entries.Select(segments => segments[0]).Distinct(StringComparer.Ordinal).Count() == 1;
        }

        private static string[] SplitEntryKey(string entryKey) =>
            entryKey.Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Where(segment => segment != ".")
                .ToArray();
    }
}
