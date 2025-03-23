using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using Requests;
using Tubifarry.Core.Utilities;
using YouTubeMusicAPI.Client;

namespace Tubifarry.Download.Clients.YouTube
{
    /// <summary>
    /// Represents an interface for a YouTube proxy.
    /// This interface defines the contract for any class that acts as a proxy for handling YouTube requests.
    /// </summary>
    public interface IYoutubeDownloadManager
    {
        public Task<string> Download(RemoteAlbum remoteAlbum, IIndexer indexer, NamingConfig namingConfig, YoutubeClient provider);
        public IEnumerable<DownloadClientItem> GetItems();
        public void RemoveItem(DownloadClientItem item);
        public void SetAuth(YoutubeProviderSettings settings);
    }

    public class YoutubeDownloadManager : IYoutubeDownloadManager
    {
        private readonly RequestContainer<YouTubeAlbumRequest> _queue;
        private readonly Logger _logger;
        private YouTubeMusicClient _ytClient;
        private Task? _testTask;

        /// <summary>
        /// Initializes a new instance of the <see cref="YoutubeDownloadManager"/> class.
        /// </summary>
        public YoutubeDownloadManager(Logger logger)
        {
            _logger = logger;
            _queue = new();
            _ytClient = new YouTubeMusicClient();
        }

        /// <summary>
        /// Sets authentication for the YouTube Music client.
        /// </summary>
        public void SetAuth(YoutubeProviderSettings settings)
        {
            if (settings.CookiePath == null && settings.PoToken == null &&
                settings.VisitorData == null && settings.TrustedSessionGeneratorUrl == null)
                return;

            _ytClient = TrustedSessionHelper.CreateAuthenticatedClientAsync(
                    settings.TrustedSessionGeneratorUrl,
                    settings.PoToken,
                    settings.VisitorData,
                    settings.CookiePath,
                    logger: _logger
                ).Result;
        }


        public Task<string> Download(RemoteAlbum remoteAlbum, IIndexer indexer, NamingConfig namingConfig, YoutubeClient provider)
        {
            _testTask ??= provider.TestFFmpeg();
            _testTask.Wait();
            YouTubeAlbumRequest request = new(remoteAlbum, new()
            {
                YouTubeMusicClient = _ytClient,
                Handler = RequestHandler.MainRequestHandlers[1],
                TryIncludeSycLrc = provider.Settings.SaveSyncedLyrics,
                TryIncludeLrc = provider.Settings.UseLRCLIB,
                DownloadPath = provider.Settings.DownloadPath,
                Chunks = provider.Settings.Chunks,
                DelayBetweenAttemps = TimeSpan.FromSeconds(5),
                NumberOfAttempts = 2,
                RandomDelayMin = provider.Settings.RandomDelayMin,
                RandomDelayMax = provider.Settings.RandomDelayMax,
                MaxDownloadSpeed = provider.Settings.MaxDownloadSpeed * 1024,
                NameingConfig = namingConfig,
                LRCLIBInstance = provider.Settings.LRCLIBInstance,
                UseID3v2_3 = provider.Settings.UseID3v2_3,
                ReEncodeOptions = (ReEncodeOptions)provider.Settings.ReEncode,
                ClientInfo = DownloadClientItemClientInfo.FromDownloadClient(provider, false)
            });
            _queue.Add(request);
            _logger.Trace($"Download request added to queue. Request ID: {request.ID}");
            return Task.FromResult(request.ID);
        }

        public IEnumerable<DownloadClientItem> GetItems() => _queue.Select(x => x.ClientItem);

        public void RemoveItem(DownloadClientItem item)
        {
            YouTubeAlbumRequest? req = _queue.ToList().Find(x => x.ID == item.DownloadId);
            if (req == null)
                return;
            req.Dispose();
            _queue.Remove(req);
            _logger.Trace($"Item removed from queue. Download ID: {item.DownloadId}");
        }
    }
}