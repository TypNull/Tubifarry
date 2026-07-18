using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using Tubifarry.Core.FFmpeg;
using Tubifarry.Download.Base;
using Tubifarry.Indexers.DABMusic;

namespace Tubifarry.Download.Clients.DABMusic
{
    public interface IDABMusicDownloadManager : IBaseDownloadManager<DABMusicDownloadRequest, DABMusicDownloadOptions, DABMusicClient>
    { }

    public class DABMusicDownloadManager(IDABMusicSessionManager sessionManager, IEnumerable<IHttpRequestInterceptor> requestInterceptors, IAudioProcessingService audioProcessing, Logger logger) : BaseDownloadManager<DABMusicDownloadRequest, DABMusicDownloadOptions, DABMusicClient>(logger), IDABMusicDownloadManager
    {
        protected override async Task<DABMusicDownloadRequest> CreateDownloadRequest(
            RemoteAlbum remoteAlbum,
            IIndexer indexer,
            NamingConfig namingConfig,
            DABMusicClient provider)
        {
            string baseUrl = provider.Settings.BaseUrl;
            bool isTrack = remoteAlbum.Release.DownloadUrl.Contains("/track/");
            string itemId = remoteAlbum.Release.DownloadUrl.Split('/').Last();

            _logger.Trace($"Type from URL: {(isTrack ? "Track" : "Album")}, Extracted ID: {itemId}");

            DABMusicDownloadOptions options = new()
            {
                Handler = _requesthandler,
                AudioProcessing = audioProcessing,
                DownloadPath = provider.Settings.DownloadPath,
                BaseUrl = baseUrl,
                MaxDownloadSpeed = provider.Settings.MaxDownloadSpeed * 1024, // Convert KB/s to bytes/s
                ConnectionRetries = provider.Settings.ConnectionRetries,
                NamingConfig = namingConfig,
                RequestInterceptors = requestInterceptors,
                DelayBetweenAttemps = TimeSpan.FromSeconds(2),
                NumberOfAttempts = (byte)provider.Settings.ConnectionRetries,
                ClientInfo = DownloadClientItemClientInfo.FromDownloadClient(provider, false),
                IsTrack = isTrack,
                ItemId = itemId,
                Email = provider.Settings.Email,
                Password = provider.Settings.Password
            };

            _requesthandler.MaxParallelism = provider.Settings.MaxParallelDownloads;
            return new DABMusicDownloadRequest(remoteAlbum, sessionManager, options);
        }
    }
}