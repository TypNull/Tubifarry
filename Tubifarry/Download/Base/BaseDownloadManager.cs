using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using Requests;

namespace Tubifarry.Download.Base
{
    /// <summary>
    /// Generic interface for download managers
    /// </summary>
    public interface IBaseDownloadManager<TDownloadRequest, TOptions, TClient>
        where TDownloadRequest : BaseDownloadRequest<TOptions>
        where TOptions : BaseDownloadOptions, new()
    {
        Task<string> Download(RemoteAlbum remoteAlbum, IIndexer indexer, NamingConfig namingConfig, TClient provider);
        IEnumerable<DownloadClientItem> GetItems();
        void RemoveItem(DownloadClientItem item);
    }

    /// <summary>
    /// Generic base download manager implementation with common functionality
    /// </summary>
    public abstract class BaseDownloadManager<TDownloadRequest, TOptions, TClient> : IBaseDownloadManager<TDownloadRequest, TOptions, TClient>
        where TDownloadRequest : BaseDownloadRequest<TOptions>
        where TOptions : BaseDownloadOptions, new()
    {
        private readonly RequestContainer<TDownloadRequest> _queue;
        protected readonly Logger _logger;
        protected readonly RequestHandler _requesthandler;

        protected BaseDownloadManager(Logger logger)
        {
            _queue = new RequestContainer<TDownloadRequest>();
            _requesthandler = new RequestHandler();
            _logger = logger;
        }

        /// <summary>
        /// Factory method to create download request instances
        /// </summary>
        protected abstract Task<TDownloadRequest> CreateDownloadRequest(RemoteAlbum remoteAlbum, IIndexer indexer, NamingConfig namingConfig, TClient provider);

        /// <summary>
        /// Common implementation for downloading with error handling
        /// </summary>
        public virtual async Task<string> Download(RemoteAlbum remoteAlbum, IIndexer indexer, NamingConfig namingConfig, TClient provider)
        {
            try
            {
                TDownloadRequest downloadRequest = await CreateDownloadRequest(remoteAlbum, indexer, namingConfig, provider);
                _queue.Add(downloadRequest);

                _logger.Debug($"Added download: {downloadRequest.ID} | {remoteAlbum.Release.Title}");
                return downloadRequest.ID;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error adding download for album: {remoteAlbum.Release.Title}");
                throw;
            }
        }

        /// <summary>
        /// Common implementation for getting all download items
        /// </summary>
        public virtual IEnumerable<DownloadClientItem> GetItems() => _queue.Select(x => x.ClientItem);

        /// <summary>
        /// Common implementation for removing download items with proper error handling
        /// </summary>
        public virtual void RemoveItem(DownloadClientItem item)
        {
            try
            {
                TDownloadRequest? request = _queue.ToList().Find(x => x.ID == item.DownloadId);
                if (request == null)
                {
                    _logger.Warn($"Attempted to remove non-existent download item: {item.DownloadId}");
                    return;
                }
                request.Dispose();
                _queue.Remove(request);
                _logger.Debug($"Removed download: {item.DownloadId}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error removing download item: {item.DownloadId}");
            }
        }
    }
}