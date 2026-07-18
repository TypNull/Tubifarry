using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using SharpCompress.Readers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Tubifarry.Core.FFmpeg
{
    public interface IFFmpegBinaryDownloader
    {
        Task DownloadAsync(string targetDirectory, CancellationToken token = default);
    }

    public sealed class FFmpegBinaryDownloader(IHttpClient httpClient, IDiskProvider diskProvider, Logger logger) : IFFmpegBinaryDownloader
    {
        private const string PinnedReleaseBranch = "8.1";

        private const string BtbNReleaseBaseUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/";
        private const string MacOsReleaseBaseUrl = "https://ffmpeg.martin-riedl.de/redirect/latest/macos/";
        private const string StaticLinuxReleaseBaseUrl = "https://johnvansickle.com/ffmpeg/releases/";

        private static readonly string[] BinaryNames = ["ffmpeg", "ffprobe"];

        public async Task DownloadAsync(string targetDirectory, CancellationToken token = default)
        {
            diskProvider.CreateFolder(targetDirectory);

            try
            {
                if (OperatingSystem.IsMacOS())
                    await DownloadArchivesAsync(GetMacOsArchiveUrls(), targetDirectory);
                else if (OperatingSystem.IsLinux() && IsMuslLibc())
                    await DownloadArchivesAsync([GetStaticLinuxArchiveUrl()], targetDirectory);
                else
                    await DownloadArchivesAsync([GetBtbNArchiveUrl()], targetDirectory);

                MarkBinariesExecutable(targetDirectory);
                logger.Info("FFmpeg {0} installed to {1}", PinnedReleaseBranch, targetDirectory);
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Pinned FFmpeg {0} download failed, falling back to ffbinaries (older version)", PinnedReleaseBranch);
                await DownloadWithFFBinariesFallbackAsync(targetDirectory);
            }
        }

        private async Task DownloadArchivesAsync(IEnumerable<string> urls, string targetDirectory)
        {
            foreach (string url in urls)
            {
                string archivePath = Path.Combine(Path.GetTempPath(), Path.GetFileName(new Uri(url).AbsolutePath));

                try
                {
                    logger.Info("Downloading FFmpeg from {0}", url);
                    await DownloadFileWithBrowserUserAgentAsync(url, archivePath);
                    ExtractBinariesFromArchive(archivePath, targetDirectory);
                }
                finally
                {
                    if (diskProvider.FileExists(archivePath))
                        diskProvider.DeleteFile(archivePath);
                }
            }
        }

        private async Task DownloadFileWithBrowserUserAgentAsync(string url, string destinationPath)
        {
            await using FileStream fileStream = new(destinationPath, FileMode.Create, FileAccess.ReadWrite);

            HttpRequest request = new(url)
            {
                AllowAutoRedirect = true,
                ResponseStream = fileStream,
                RequestTimeout = TimeSpan.FromSeconds(300)
            };
            request.Headers.Add("User-Agent", Tubifarry.UserAgent);

            HttpResponse response = await httpClient.GetAsync(request);

            if (response.Headers.ContentType?.Contains("text/html") == true)
                throw new HttpException(request, response, "Site responded with html content instead of an archive.");
        }

        private static bool IsMuslLibc() =>
            RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.OrdinalIgnoreCase)
            || File.Exists("/lib/ld-musl-x86_64.so.1")
            || File.Exists("/lib/ld-musl-aarch64.so.1");

        private static string GetStaticLinuxArchiveUrl()
        {
            string architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "amd64";
            return $"{StaticLinuxReleaseBaseUrl}ffmpeg-release-{architecture}-static.tar.xz";
        }

        private static string GetBtbNArchiveUrl()
        {
            bool isArm64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

            if (OperatingSystem.IsWindows())
                return BtbNReleaseBaseUrl + (isArm64
                    ? $"ffmpeg-n{PinnedReleaseBranch}-latest-winarm64-gpl-{PinnedReleaseBranch}.zip"
                    : $"ffmpeg-n{PinnedReleaseBranch}-latest-win64-gpl-{PinnedReleaseBranch}.zip");

            if (OperatingSystem.IsLinux())
                return BtbNReleaseBaseUrl + (isArm64
                    ? $"ffmpeg-n{PinnedReleaseBranch}-latest-linuxarm64-gpl-{PinnedReleaseBranch}.tar.xz"
                    : $"ffmpeg-n{PinnedReleaseBranch}-latest-linux64-gpl-{PinnedReleaseBranch}.tar.xz");

            throw new PlatformNotSupportedException($"No pinned FFmpeg build available for {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})");
        }

        private static IEnumerable<string> GetMacOsArchiveUrls()
        {
            string architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "amd64";
            return BinaryNames.Select(binaryName => $"{MacOsReleaseBaseUrl}{architecture}/release/{binaryName}.zip");
        }

        private static void ExtractBinariesFromArchive(string archivePath, string targetDirectory)
        {
            using FileStream archiveStream = File.OpenRead(archivePath);
            using IReader reader = ReaderFactory.OpenReader(archiveStream);

            while (reader.MoveToNextEntry())
            {
                if (reader.Entry.IsDirectory)
                    continue;

                string fileName = Path.GetFileName(reader.Entry.Key ?? string.Empty);

                bool isBinary = BinaryNames.Contains(fileName, StringComparer.OrdinalIgnoreCase)
                    || (Path.GetExtension(fileName).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                        && BinaryNames.Contains(Path.GetFileNameWithoutExtension(fileName), StringComparer.OrdinalIgnoreCase));

                if (!isBinary)
                    continue;

                string destinationPath = Path.Combine(targetDirectory, fileName);
                using FileStream destination = File.Create(destinationPath);
                using Stream source = reader.OpenEntryStream();
                source.CopyTo(destination);
            }
        }

        private void MarkBinariesExecutable(string targetDirectory)
        {
            if (OperatingSystem.IsWindows())
                return;

            foreach (string binaryName in BinaryNames)
            {
                string binaryPath = Path.Combine(targetDirectory, binaryName);
                if (diskProvider.FileExists(binaryPath))
                    diskProvider.SetFilePermissions(binaryPath, "755", null!);
            }
        }

        private async Task DownloadWithFFBinariesFallbackAsync(string targetDirectory)
        {
            HttpResponse versionResponse = httpClient.Get(new HttpRequest("https://ffbinaries.com/api/v1/version/latest"));

            using JsonDocument document = JsonDocument.Parse(versionResponse.Content);
            JsonElement binaries = document.RootElement.GetProperty("bin").GetProperty(GetFFBinariesPlatform());

            List<string> archiveUrls = [];
            foreach (string binaryName in BinaryNames)
            {
                if (binaries.TryGetProperty(binaryName, out JsonElement urlElement) && urlElement.GetString() is { } url)
                    archiveUrls.Add(url);
            }

            if (archiveUrls.Count == 0)
                throw new InvalidOperationException("ffbinaries API returned no download urls for this platform");

            await DownloadArchivesAsync(archiveUrls, targetDirectory);
            MarkBinariesExecutable(targetDirectory);
            logger.Info("FFmpeg installed via ffbinaries fallback to {0}", targetDirectory);
        }

        private static string GetFFBinariesPlatform()
        {
            if (OperatingSystem.IsWindows())
                return "windows-64";
            if (OperatingSystem.IsMacOS())
                return "osx-64";
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-64";
        }
    }
}
