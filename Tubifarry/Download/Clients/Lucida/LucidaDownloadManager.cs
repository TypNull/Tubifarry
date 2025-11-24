using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using Tubifarry.Download.Base;
using Tubifarry.Indexers.Lucida;

namespace Tubifarry.Download.Clients.Lucida
{
    /// <summary>
    /// Interface for the Lucida download manager
    /// </summary>
    public interface ILucidaDownloadManager : IBaseDownloadManager<LucidaDownloadRequest, BaseDownloadOptions, LucidaClient>;

    /// <summary>
    /// Lucida download manager using the base download manager implementation
    /// </summary>
    public class LucidaDownloadManager(Logger logger, IEnumerable<IHttpRequestInterceptor> requestInterceptors) : BaseDownloadManager<LucidaDownloadRequest, BaseDownloadOptions, LucidaClient>(logger), ILucidaDownloadManager
    {
        protected override async Task<LucidaDownloadRequest> CreateDownloadRequest(
            RemoteAlbum remoteAlbum,
            IIndexer indexer,
            NamingConfig namingConfig,
            LucidaClient provider)
        {
            string itemUrl = remoteAlbum.Release.DownloadUrl;
            string baseUrl = ((LucidaIndexerSettings)indexer.Definition.Settings).BaseUrl;

            _logger.Trace($"Processing Lucida download URL: {itemUrl} on Instance: {baseUrl}");

            bool isTrack = remoteAlbum.Release.Source == "track";
            _logger.Trace($"Type from Source field: {remoteAlbum.Release.Source} -> {(isTrack ? "Track" : "Album")}");

            BaseDownloadOptions options = new()
            {
                Handler = _requesthandler,
                DownloadPath = provider.Settings.DownloadPath,
                BaseUrl = baseUrl,
                RequestTimeout = provider.Settings.RequestTimeout,
                MaxDownloadSpeed = provider.Settings.MaxDownloadSpeed * 1024, // Convert KB/s to bytes/s
                ConnectionRetries = provider.Settings.ConnectionRetries,
                NamingConfig = namingConfig,
                DelayBetweenAttemps = TimeSpan.FromSeconds(2),
                RequestInterceptors = requestInterceptors,
                NumberOfAttempts = (byte)provider.Settings.ConnectionRetries,
                ClientInfo = DownloadClientItemClientInfo.FromDownloadClient(provider, false),
                IsTrack = isTrack,
                ItemId = itemUrl
            };

            _requesthandler.MaxParallelism = provider.Settings.MaxParallelDownloads;
            await Task.Yield();
            return new LucidaDownloadRequest(remoteAlbum, options);
        }
    }
}
