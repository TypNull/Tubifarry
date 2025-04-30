using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using Requests;

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
                string downloadUrl = remoteAlbum.Release.DownloadUrl;
                _logger.Debug($"Processing Lucida download URL: {downloadUrl}");

                // Determine type from CustomString (Source field)
                bool isTrack = remoteAlbum.Release.Source.Equals("T", StringComparison.OrdinalIgnoreCase);
                string actualUrl = remoteAlbum.Release.DownloadUrl;
                _logger.Debug($"Type from Source field: {remoteAlbum.Release.Source} -> {(isTrack ? "Track" : "Album")}");

                // Create download options from provider settings
                LucidaDownloadOptions options = new()
                {
                    Handler = RequestHandler.MainRequestHandlers[1],
                    DownloadPath = provider.Settings.DownloadPath,
                    BaseUrl = provider.Settings.BaseUrl,
                    RequestTimeout = provider.Settings.RequestTimeout,
                    MaxDownloadSpeed = provider.Settings.MaxDownloadSpeed * 1024, // Convert KB/s to bytes/s
                    ConnectionRetries = provider.Settings.ConnectionRetries,
                    NamingConfig = namingConfig,
                    DelayBetweenAttemps = TimeSpan.FromSeconds(2),
                    NumberOfAttempts = (byte)provider.Settings.ConnectionRetries,
                    ClientInfo = DownloadClientItemClientInfo.FromDownloadClient(provider, false),
                    IsTrack = isTrack,
                    ActualUrl = actualUrl,
                    ExtractAlbums = provider.Settings.ExtractAlbums,
                    KeepArchiveFiles = provider.Settings.KeepArchiveFiles
                };

                // Update max parallelism if needed
                if (RequestHandler.MainRequestHandlers[1].MaxParallelism != provider.Settings.MaxParallelDownloads)
                {
                    RequestHandler.MainRequestHandlers[1].MaxParallelism = provider.Settings.MaxParallelDownloads;
                    _logger.Debug($"Updated max parallel downloads to {provider.Settings.MaxParallelDownloads}");
                }

                // Create and queue the download request
                LucidaDownloadRequest request = new(remoteAlbum, options);
                _queue.Add(request);

                _logger.Info($"Lucida download request created for '{remoteAlbum.Release.Title}'. Request ID: {request.ID}");
                return request.ID;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error creating Lucida download request for '{remoteAlbum.Release.Title}'");
                throw;
            }
        }

        public IEnumerable<DownloadClientItem> GetItems()
        {
            try
            {
                return _queue.Select(x => x.ClientItem);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error retrieving download items");
                return Enumerable.Empty<DownloadClientItem>();
            }
        }

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

                // Dispose the request to clean up resources
                request.Dispose();
                _queue.Remove(request);

                _logger.Debug($"Removed Lucida download item: {item.DownloadId} - {item.Title}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error removing download item: {item.DownloadId}");
            }
        }
    }
}
