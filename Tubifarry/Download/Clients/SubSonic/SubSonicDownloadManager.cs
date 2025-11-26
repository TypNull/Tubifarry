using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using Tubifarry.Download.Base;

namespace Tubifarry.Download.Clients.SubSonic
{
    public interface ISubSonicDownloadManager : IBaseDownloadManager<SubSonicDownloadRequest, SubSonicDownloadOptions, SubSonicClient>
    { }

    /// <summary>
    /// Manager for SubSonic downloads, handles creating and managing download requests
    /// </summary>
    public class SubSonicDownloadManager(IEnumerable<IHttpRequestInterceptor> requestInterceptors, Logger logger) : BaseDownloadManager<SubSonicDownloadRequest, SubSonicDownloadOptions, SubSonicClient>(logger), ISubSonicDownloadManager
    {
        private readonly IEnumerable<IHttpRequestInterceptor> _requestInterceptors = requestInterceptors;

        protected override async Task<SubSonicDownloadRequest> CreateDownloadRequest(
            RemoteAlbum remoteAlbum,
            IIndexer indexer,
            NamingConfig namingConfig,
            SubSonicClient provider)
        {
            string baseUrl = provider.Settings.ServerUrl.TrimEnd('/');
            string downloadUrl = remoteAlbum.Release.DownloadUrl;

            if (downloadUrl.StartsWith(baseUrl))
                _logger.Warn("The expected URL does not match the configured API URL.");

            bool isTrack = downloadUrl.Contains("/track/");
            string itemId = ExtractIdFromUrl(downloadUrl);

            _logger.Trace($"Type from URL: {(isTrack ? "Track" : "Album")}, Extracted ID: {itemId}");

            SubSonicDownloadOptions options = new()
            {
                Handler = _requesthandler,
                DownloadPath = provider.Settings.DownloadPath,
                BaseUrl = baseUrl,
                Username = provider.Settings.Username,
                Password = provider.Settings.Password,
                UseTokenAuth = provider.Settings.UseTokenAuth,
                MaxDownloadSpeed = provider.Settings.MaxDownloadSpeed * 1024, // Convert KB/s to bytes/s
                ConnectionRetries = provider.Settings.ConnectionRetries,
                RequestTimeout = provider.Settings.RequestTimeout,
                NamingConfig = namingConfig,
                RequestInterceptors = _requestInterceptors,
                DelayBetweenAttemps = TimeSpan.FromSeconds(2),
                NumberOfAttempts = (byte)provider.Settings.ConnectionRetries,
                ClientInfo = DownloadClientItemClientInfo.FromDownloadClient(provider, false),
                IsTrack = isTrack,
                ItemId = itemId,
                PreferredFormat = (PreferredFormatEnum)provider.Settings.PreferredFormat,
                MaxBitRate = provider.Settings.MaxBitRate
            };

            _requesthandler.MaxParallelism = provider.Settings.MaxParallelDownloads;

            return await Task.FromResult(new SubSonicDownloadRequest(remoteAlbum, options));
        }

        private static string ExtractIdFromUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
                return string.Empty;

            // The pattern is: {baseUrl}://album/{id} or {baseUrl}://track/{id}
            int albumPos = url.IndexOf("/album/", StringComparison.OrdinalIgnoreCase);
            if (albumPos >= 0)
                return url[(albumPos + "/album/".Length)..];

            int trackPos = url.IndexOf("/track/", StringComparison.OrdinalIgnoreCase);
            if (trackPos >= 0)
                return url[(trackPos + "/track/".Length)..];

            // Fallback: try to extract from path
            string[] parts = url.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
                return parts[^1];

            return string.Empty;
        }
    }
}