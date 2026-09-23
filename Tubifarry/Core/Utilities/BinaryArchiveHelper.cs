using NzbDrone.Common.Http;
using SharpCompress.Readers;
using System.Runtime.InteropServices;

namespace Tubifarry.Core.Utilities
{
    public static class BinaryArchiveHelper
    {
        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(300);

        public static bool IsMuslLibc() =>
            RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.OrdinalIgnoreCase)
            || File.Exists("/etc/alpine-release")
            || File.Exists("/lib/ld-musl-x86_64.so.1")
            || File.Exists("/lib/ld-musl-aarch64.so.1")
            || File.Exists("/lib/ld-musl-armhf.so.1");

        public static string CreateTempArchivePath(string url) =>
            Path.Combine(Path.GetTempPath(), $"tubifarry-{Guid.NewGuid():N}-{Path.GetFileName(new Uri(url).AbsolutePath)}");

        public static async Task DownloadAsync(IHttpClient httpClient, string url, string destinationPath)
        {
            await using FileStream fileStream = new(destinationPath, FileMode.Create, FileAccess.ReadWrite);

            HttpRequest request = new(url)
            {
                AllowAutoRedirect = true,
                ResponseStream = fileStream,
                RequestTimeout = DownloadTimeout
            };
            request.Headers.Add("User-Agent", Tubifarry.UserAgent);

            HttpResponse response = await httpClient.GetAsync(request);

            if (response.Headers.ContentType?.Contains("text/html") == true)
                throw new HttpException(request, response, "Site responded with html content instead of an archive.");
        }

        public static List<string> ListFileEntries(string archivePath)
        {
            List<string> entries = [];

            using FileStream archiveStream = File.OpenRead(archivePath);
            using IReader reader = ReaderFactory.OpenReader(archiveStream);

            while (reader.MoveToNextEntry())
            {
                if (!reader.Entry.IsDirectory && !string.IsNullOrEmpty(reader.Entry.Key))
                    entries.Add(reader.Entry.Key);
            }

            return entries;
        }

        public static void ExtractEntries(string archivePath, Func<string, string?> getDestinationPath)
        {
            using FileStream archiveStream = File.OpenRead(archivePath);
            using IReader reader = ReaderFactory.OpenReader(archiveStream);

            while (reader.MoveToNextEntry())
            {
                if (reader.Entry.IsDirectory)
                    continue;

                string? destinationPath = getDestinationPath(reader.Entry.Key ?? string.Empty);
                if (destinationPath == null)
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                using FileStream destination = File.Create(destinationPath);
                using Stream source = reader.OpenEntryStream();
                source.CopyTo(destination);
            }
        }
    }
}
