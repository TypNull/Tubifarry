using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using Tubifarry.Core.FFmpeg;
using Tubifarry.Download.Base;
using Tubifarry.Download.Clients.YouTube;

namespace Tubifarry.Download.Clients.Invidious
{
    public interface IInvidiousDownloadManager : IBaseDownloadManager<InvidiousDownloadRequest, InvidiousDownloadOptions, InvidiousClient> { }

    public class InvidiousDownloadManager(IEnumerable<IHttpRequestInterceptor> requestInterceptors, IAudioProcessingService audioProcessing, Logger logger)
        : BaseDownloadManager<InvidiousDownloadRequest, InvidiousDownloadOptions, InvidiousClient>(logger), IInvidiousDownloadManager
    {
        protected override Task<InvidiousDownloadRequest> CreateDownloadRequest(
            RemoteAlbum remoteAlbum,
            IIndexer indexer,
            NamingConfig namingConfig,
            InvidiousClient provider)
        {
            string baseUrl = provider.Settings.BaseUrl.TrimEnd('/');

            InvidiousDownloadOptions options = new()
            {
                Handler = _requesthandler,
                AudioProcessing = audioProcessing,
                DownloadPath = provider.Settings.DownloadPath,
                BaseUrl = baseUrl,
                MaxDownloadSpeed = provider.Settings.MaxDownloadSpeed * 1024,
                ConnectionRetries = provider.Settings.ConnectionRetries,
                NamingConfig = namingConfig,
                RequestInterceptors = requestInterceptors,
                DelayBetweenAttemps = TimeSpan.FromSeconds(2),
                NumberOfAttempts = (byte)provider.Settings.ConnectionRetries,
                ClientInfo = DownloadClientItemClientInfo.FromDownloadClient(provider, false),
                IsTrack = false,
                ItemId = remoteAlbum.Release.DownloadUrl,
                ProxyVideos = provider.Settings.ProxyVideos,
                ReEncodeOptions = (ReEncodeOptions)provider.Settings.ReEncode,
                UseID3v2_3 = provider.Settings.UseID3v2_3,
                UseSponsorBlock = provider.Settings.UseSponsorBlock,
                SponsorBlockApiEndpoint = provider.Settings.SponsorBlockApiEndpoint
            };

            _requesthandler.MaxParallelism = provider.Settings.MaxParallelDownloads;
            return Task.FromResult(new InvidiousDownloadRequest(remoteAlbum, options));
        }
    }
}
