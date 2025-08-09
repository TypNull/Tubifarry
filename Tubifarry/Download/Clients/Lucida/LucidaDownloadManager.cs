using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using Requests;
using Tubifarry.Indexers.Lucida;

namespace Tubifarry.Download.Clients.Lucida
{
    /// <summary>
    /// Interface for the Lucida download manager
    /// Handles queuing and processing of Lucida downloads
    /// </summary>
    public interface ILucidaDownloadManager
    {
        Task<string> Download(RemoteAlbum remoteAlbum, IIndexer indexer, NamingConfig namingConfig, LucidaClient provider);
        IEnumerable<DownloadClientItem> GetItems();
        void RemoveItem(DownloadClientItem item);
    }

    /// <summary>
    /// Manages Lucida downloads using the request system
    /// Reads type from CustomString field - no URL parsing needed
    /// </summary>
    public class LucidaDownloadManager : ILucidaDownloadManager
    {
        private readonly RequestContainer<LucidaDownloadRequest> _queue;
        private readonly Logger _logger;

        public LucidaDownloadManager(Logger logger)
        {
            _logger = logger;
            _queue = new RequestContainer<LucidaDownloadRequest>();
        }

        public async Task<string> Download(RemoteAlbum remoteAlbum, IIndexer indexer, NamingConfig namingConfig, LucidaClient provider)
        {
            try
            {
                string itemUrl = remoteAlbum.Release.DownloadUrl;
                string baseUrl = ((LucidaIndexerSettings)indexer.Definition.Settings).BaseUrl;
                _logger.Debug($"Processing Lucida download URL: {itemUrl} on Instance: {baseUrl}");

                bool isTrack = remoteAlbum.Release.Source == "track";
                _logger.Debug($"Type from Source field: {remoteAlbum.Release.Source} -> {(isTrack ? "Track" : "Album")}");

                LucidaDownloadOptions options = new()
                {
                    Handler = RequestHandler.MainRequestHandlers[1],
                    DownloadPath = provider.Settings.DownloadPath,
                    BaseUrl = baseUrl,
                    RequestTimeout = provider.Settings.RequestTimeout,
                    MaxDownloadSpeed = provider.Settings.MaxDownloadSpeed * 1024, // Convert KB/s to bytes/s
                    ConnectionRetries = provider.Settings.ConnectionRetries,
                    NamingConfig = namingConfig,
                    DelayBetweenAttemps = TimeSpan.FromSeconds(2),
                    NumberOfAttempts = (byte)provider.Settings.ConnectionRetries,
                    ClientInfo = DownloadClientItemClientInfo.FromDownloadClient(provider, false),
                    IsTrack = isTrack,
                    ItemUrl = itemUrl
                };

                RequestHandler.MainRequestHandlers[1].MaxParallelism = provider.Settings.MaxParallelDownloads;
                LucidaDownloadRequest request = new(remoteAlbum, options);
                _queue.Add(request);
                return request.ID;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error creating Lucida download request for '{remoteAlbum.Release.Title}'");
                throw;
            }
        }

        public IEnumerable<DownloadClientItem> GetItems() => _queue.Select(x => x.ClientItem);

        public void RemoveItem(DownloadClientItem item)
        {
            try
            {
                LucidaDownloadRequest? request = _queue.ToList().Find(x => x.ID == item.DownloadId);
                if (request == null)
                {
                    _logger.Warn($"Attempted to remove non-existent download item: {item.DownloadId}");
                    return;
                }

                request.Dispose();
                _queue.Remove(request);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error removing download item: {item.DownloadId}");
            }
        }
    }
}
