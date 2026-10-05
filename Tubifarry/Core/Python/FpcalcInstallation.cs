using NLog;
using NzbDrone.Common.Http;
using System.Runtime.InteropServices;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Core.Python
{
    public interface IFpcalcInstallation
    {
        Task<string?> TryEnsureAsync(string rootDirectory, CancellationToken token = default);
    }

    public sealed class FpcalcInstallation(IHttpClient httpClient, Logger logger) : PinnedBinaryInstallation(httpClient, logger), IFpcalcInstallation
    {
        public const string PinnedVersion = "1.6.0";

        private bool _unavailableLogged;

        protected override string ToolName => "fpcalc";
        protected override string Version => PinnedVersion;
        protected override string ExecutableBaseName => "fpcalc";
        protected override IReadOnlyList<string> VersionArguments => ["-version"];

        public async Task<string?> TryEnsureAsync(string rootDirectory, CancellationToken token = default)
        {
            try
            {
                return await EnsureExecutableAsync(rootDirectory, token);
            }
            catch (InvalidOperationException ex)
            {
                if (!_unavailableLogged)
                    Logger.Warn("Acoustic fingerprints are unavailable, beets will match by tags only: {0}", ex.Message);
                _unavailableLogged = true;
                return null;
            }
        }

        protected override string GetDownloadUrl(string assetName) =>
            $"https://github.com/acoustid/chromaprint/releases/download/v{PinnedVersion}/{assetName}";

        protected override string? GetAssetName()
        {
            Architecture architecture = RuntimeInformation.ProcessArchitecture;

            string? platform = true switch
            {
                _ when OperatingSystem.IsWindows() && architecture is Architecture.X64 or Architecture.Arm64 => "windows-x86_64",
                _ when OperatingSystem.IsMacOS() => "macos-universal",
                _ when OperatingSystem.IsLinux() && architecture == Architecture.X64 => "linux-x86_64",
                _ when OperatingSystem.IsLinux() && architecture == Architecture.Arm64 => "linux-arm64",
                _ => null
            };

            return platform == null ? null : OperatingSystem.IsWindows()
                ? $"chromaprint-fpcalc-{PinnedVersion}-{platform}.zip"
                : $"chromaprint-fpcalc-{PinnedVersion}-{platform}.tar.gz";
        }
    }
}
