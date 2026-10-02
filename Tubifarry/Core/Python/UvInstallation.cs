using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using System.Runtime.InteropServices;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Core.Python
{
    public interface IUvInstallation
    {
        string DefaultRootDirectory { get; }
        Task<string> EnsureAsync(string rootDirectory, CancellationToken token = default);
    }

    public sealed class UvInstallation(IHttpClient httpClient, IAppFolderInfo appFolderInfo, Logger logger) : PinnedBinaryInstallation(httpClient, logger), IUvInstallation
    {
        public const string PinnedVersion = "0.12.23";

        public string DefaultRootDirectory =>
            Path.Combine(appFolderInfo.GetPluginPath(), PluginInfo.Author, PluginInfo.Name, "python");

        protected override string ToolName => "uv";
        protected override string Version => PinnedVersion;
        protected override string ExecutableBaseName => "uv";
        protected override IReadOnlyList<string> VersionArguments => ["--version"];

        public Task<string> EnsureAsync(string rootDirectory, CancellationToken token = default) =>
            EnsureExecutableAsync(rootDirectory, token);

        protected override string GetDownloadUrl(string assetName) =>
            $"https://github.com/astral-sh/uv/releases/download/{PinnedVersion}/{assetName}";

        protected override string? GetAssetName()
        {
            Architecture architecture = RuntimeInformation.ProcessArchitecture;

            string? triple = true switch
            {
                _ when OperatingSystem.IsWindows() => architecture switch
                {
                    Architecture.X64 => "x86_64-pc-windows-msvc",
                    Architecture.Arm64 => "aarch64-pc-windows-msvc",
                    Architecture.X86 => "i686-pc-windows-msvc",
                    _ => null
                },
                _ when OperatingSystem.IsMacOS() => architecture switch
                {
                    Architecture.X64 => "x86_64-apple-darwin",
                    Architecture.Arm64 => "aarch64-apple-darwin",
                    _ => null
                },
                _ when OperatingSystem.IsLinux() => architecture switch
                {
                    Architecture.X64 => "x86_64-unknown-linux-musl",
                    Architecture.Arm64 => "aarch64-unknown-linux-musl",
                    Architecture.Arm => "armv7-unknown-linux-musleabihf",
                    Architecture.X86 => "i686-unknown-linux-musl",
                    _ => null
                },
                _ => null
            };

            return triple == null ? null : OperatingSystem.IsWindows() ? $"uv-{triple}.zip" : $"uv-{triple}.tar.gz";
        }
    }
}
