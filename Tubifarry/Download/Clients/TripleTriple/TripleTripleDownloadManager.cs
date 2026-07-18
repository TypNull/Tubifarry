using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using Tubifarry.Core.FFmpeg;
using Tubifarry.Download.Base;
using Tubifarry.Indexers.TripleTriple;

namespace Tubifarry.Download.Clients.TripleTriple
{
    public interface ITripleTripleDownloadManager : IBaseDownloadManager<TripleTripleDownloadRequest, TripleTripleDownloadOptions, TripleTripleClient> { }

    public class TripleTripleDownloadManager : BaseDownloadManager<TripleTripleDownloadRequest, TripleTripleDownloadOptions, TripleTripleClient>, ITripleTripleDownloadManager
    {
        private readonly IEnumerable<IHttpRequestInterceptor> _requestInterceptors;
        private readonly IAudioProcessingService _audioProcessing;

        public TripleTripleDownloadManager(IEnumerable<IHttpRequestInterceptor> requestInterceptors, IAudioProcessingService audioProcessing, Logger logger) : base(logger)
        {
            _requestInterceptors = requestInterceptors;
            _audioProcessing = audioProcessing;
        }

        protected override Task<TripleTripleDownloadRequest> CreateDownloadRequest(
            RemoteAlbum remoteAlbum,
            IIndexer indexer,
            NamingConfig namingConfig,
            TripleTripleClient provider)
        {
            string baseUrl = provider.Settings.BaseUrl;
            bool isTrack = remoteAlbum.Release.DownloadUrl.StartsWith("track/");

            TripleTripleDownloadOptions options = new()
            {
                Handler = _requesthandler,
                AudioProcessing = _audioProcessing,
                DownloadPath = provider.Settings.DownloadPath,
                BaseUrl = baseUrl,
                MaxDownloadSpeed = provider.Settings.MaxDownloadSpeed * 1024,
                ConnectionRetries = provider.Settings.ConnectionRetries,
                NamingConfig = namingConfig,
                RequestInterceptors = _requestInterceptors,
                DelayBetweenAttemps = TimeSpan.FromSeconds(2),
                NumberOfAttempts = (byte)provider.Settings.ConnectionRetries,
                ClientInfo = DownloadClientItemClientInfo.FromDownloadClient(provider, false),
                IsTrack = isTrack,
                ItemId = remoteAlbum.Release.DownloadUrl,
                CountryCode = ((TripleTripleCountry)provider.Settings.CountryCode).ToString(),
                Codec = (TripleTripleCodec)provider.Settings.Codec,
                DownloadLyrics = provider.Settings.DownloadLyrics,
                CreateLrcFile = provider.Settings.CreateLrcFile,
                EmbedLyrics = provider.Settings.EmbedLyrics,
                CoverSize = provider.Settings.CoverSize
            };

            _requesthandler.MaxParallelism = provider.Settings.MaxParallelDownloads;
            return Task.FromResult(new TripleTripleDownloadRequest(remoteAlbum, options));
        }
    }
}
