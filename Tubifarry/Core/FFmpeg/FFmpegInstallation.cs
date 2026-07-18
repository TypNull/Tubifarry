using FFMpegCore;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Extras.Metadata;
using Tubifarry.Metadata.FFmpeg;

namespace Tubifarry.Core.FFmpeg
{
    public interface IFFmpegInstallation
    {
        string DefaultInstallDirectory { get; }
        string? ExecutablesDirectory { get; }
        bool IsInstalled();
        void UseExecutablesDirectory(string directory);
        void Reset();
        Task<bool> EnsureReadyAsync(CancellationToken token = default);
        Task EnsureInstalledAsync(string? installDirectory = null, CancellationToken token = default);
    }

    public sealed class FFmpegInstallation(IFFmpegBinaryDownloader downloader, Lazy<IMetadataFactory> metadataFactory, IAppFolderInfo appFolderInfo, Logger logger) : IFFmpegInstallation
    {
        private static readonly string[] FFmpegFileNames = ["ffmpeg", "ffmpeg.exe", "ffmpeg.bin"];
        private static readonly TimeSpan FailedInstallRetryCooldown = TimeSpan.FromMinutes(15);

        private readonly object _detectionLock = new();
        private readonly SemaphoreSlim _installGate = new(1, 1);

        private bool? _isInstalled;
        private DateTime _lastFailedInstallAttempt = DateTime.MinValue;

        public string DefaultInstallDirectory =>
            Path.Combine(appFolderInfo.GetPluginPath(), PluginInfo.Author, PluginInfo.Name, "ffmpeg");

        public string? ExecutablesDirectory => string.IsNullOrEmpty(GlobalFFOptions.Current.BinaryFolder) ? null : GlobalFFOptions.Current.BinaryFolder;

        public bool IsInstalled()
        {
            if (_isInstalled.HasValue)
                return _isInstalled.Value;

            lock (_detectionLock)
            {
                _isInstalled ??= DetectInstallation();
                return _isInstalled.Value;
            }
        }

        public void UseExecutablesDirectory(string directory)
        {
            lock (_detectionLock)
            {
                GlobalFFOptions.Configure(options => options.BinaryFolder = directory);
                _isInstalled = null;
            }
        }

        public void Reset()
        {
            lock (_detectionLock)
                _isInstalled = null;
        }

        public async Task<bool> EnsureReadyAsync(CancellationToken token = default)
        {
            if (IsInstalled())
                return true;

            FFmpegSettings? settings = GetEnabledProviderSettings();
            if (settings?.AutoDownload != true)
                return false;

            try
            {
                await EnsureInstalledAsync(NormalizeDirectory(settings.InstallDirectory), token);
                return true;
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "On-demand FFmpeg installation failed");
                return false;
            }
        }

        public async Task EnsureInstalledAsync(string? installDirectory = null, CancellationToken token = default)
        {
            if (IsInstalled())
                return;

            await _installGate.WaitAsync(token);
            try
            {
                if (IsInstalled())
                    return;

                if (DateTime.UtcNow - _lastFailedInstallAttempt < FailedInstallRetryCooldown)
                    throw new InvalidOperationException($"A recent FFmpeg installation attempt failed. Next retry is allowed after {FailedInstallRetryCooldown.TotalMinutes:0} minutes.");

                string targetDirectory = installDirectory ?? NormalizeDirectory(GetEnabledProviderSettings()?.InstallDirectory) ?? DefaultInstallDirectory;

                try
                {
                    await downloader.DownloadAsync(targetDirectory, token);

                    UseExecutablesDirectory(targetDirectory);

                    if (!IsInstalled())
                        throw new InvalidOperationException($"FFmpeg was downloaded to '{targetDirectory}' but no usable executable was found afterwards.");

                    _lastFailedInstallAttempt = DateTime.MinValue;
                }
                catch
                {
                    _lastFailedInstallAttempt = DateTime.UtcNow;
                    throw;
                }
            }
            finally
            {
                _installGate.Release();
            }
        }

        private bool DetectInstallation()
        {
            foreach (string candidate in EnumerateCandidateDirectories())
            {
                if (!ContainsFFmpegExecutable(candidate))
                    continue;

                GlobalFFOptions.Configure(options => options.BinaryFolder = candidate);
                logger.Debug("Using FFmpeg from {0}", candidate);
                return true;
            }

            logger.Trace("FFmpeg not found in configured directory, plugin directory, FFMPEG variable or PATH");
            return false;
        }

        private IEnumerable<string> EnumerateCandidateDirectories()
        {
            string? configured = GlobalFFOptions.Current.BinaryFolder;
            if (!string.IsNullOrEmpty(configured))
                yield return configured;

            string? providerDirectory = NormalizeDirectory(GetEnabledProviderSettings()?.InstallDirectory);
            if (providerDirectory != null)
                yield return providerDirectory;

            yield return DefaultInstallDirectory;

            string? environmentValue = Environment.GetEnvironmentVariable("FFMPEG");
            if (!string.IsNullOrEmpty(environmentValue))
                yield return File.Exists(environmentValue) ? Path.GetDirectoryName(environmentValue)! : environmentValue;

            foreach (string pathEntry in Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [])
                yield return pathEntry;
        }

        private FFmpegSettings? GetEnabledProviderSettings()
        {
            try
            {
                return metadataFactory.Value.Enabled()
                    .OfType<FFmpegMetadata>()
                    .Select(metadata => metadata.Definition?.Settings as FFmpegSettings)
                    .FirstOrDefault(settings => settings != null);
            }
            catch (Exception ex)
            {
                logger.Trace(ex, "Could not read FFmpeg provider settings");
                return null;
            }
        }

        private static string? NormalizeDirectory(string? directory) =>
            string.IsNullOrWhiteSpace(directory) ? null : directory;

        private static bool ContainsFFmpegExecutable(string directory)
        {
            if (!Directory.Exists(directory))
                return false;

            return Directory.GetFiles(directory)
                .Any(file => FFmpegFileNames.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase) && IsNativeExecutable(file));
        }

        private static bool IsNativeExecutable(string filePath)
        {
            try
            {
                using FileStream stream = File.OpenRead(filePath);
                Span<byte> magic = stackalloc byte[4];
                if (stream.Read(magic) < 4)
                    return false;

                bool isWindowsPE = magic[0] == 0x4D && magic[1] == 0x5A;
                bool isLinuxElf = magic[0] == 0x7F && magic[1] == 0x45 && magic[2] == 0x4C && magic[3] == 0x46;
                bool isMachO = magic[0] == 0xFE && magic[1] == 0xED && magic[2] == 0xFA && (magic[3] == 0xCE || magic[3] == 0xCF);
                bool isUniversalBinary = magic[0] == 0xCA && magic[1] == 0xFE && magic[2] == 0xBA && magic[3] == 0xBE;

                return isWindowsPE || isLinuxElf || isMachO || isUniversalBinary;
            }
            catch
            {
                return false;
            }
        }
    }
}
