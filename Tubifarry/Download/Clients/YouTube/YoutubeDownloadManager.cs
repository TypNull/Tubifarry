using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using Tubifarry.Core.Records;
using Tubifarry.Download.Base;
using YouTubeMusicAPI.Client;

namespace Tubifarry.Download.Clients.YouTube
{
    /// <summary>
    /// Interface for YouTube download manager
    /// </summary>
    public interface IYoutubeDownloadManager : IBaseDownloadManager<YouTubeDownloadRequest, YouTubeDownloadOptions, YoutubeClient>;

    /// <summary>
    /// YouTube download manager using the base download manager implementation
    /// </summary>
    public class YoutubeDownloadManager : BaseDownloadManager<YouTubeDownloadRequest, YouTubeDownloadOptions, YoutubeClient>, IYoutubeDownloadManager
    {
        private YouTubeMusicClient? _youTubeClient;
        private SessionTokens? _sessionToken;
        private Task? _testTask;

        public YoutubeDownloadManager(Logger logger) : base(logger)
        {
            _requesthandler.MaxParallelism = 2;
        }

        protected override async Task<YouTubeDownloadRequest> CreateDownloadRequest(
            RemoteAlbum remoteAlbum,
            IIndexer indexer,
            NamingConfig namingConfig,
            YoutubeClient provider)
        {
            _testTask ??= provider.TestFFmpeg();
            _testTask.Wait();
            await UpdateClientAsync(provider);

            YouTubeDownloadOptions options = new()
            {
                YouTubeMusicClient = _youTubeClient,
                Handler = _requesthandler,
                DownloadPath = provider.Settings.DownloadPath,
                Chunks = provider.Settings.Chunks,
                DelayBetweenAttemps = TimeSpan.FromSeconds(5),
                NumberOfAttempts = 2,
                RandomDelayMin = provider.Settings.RandomDelayMin,
                RandomDelayMax = provider.Settings.RandomDelayMax,
                MaxDownloadSpeed = provider.Settings.MaxDownloadSpeed * 1024,
                NamingConfig = namingConfig,
                UseID3v2_3 = provider.Settings.UseID3v2_3,
                ReEncodeOptions = (ReEncodeOptions)provider.Settings.ReEncode,
                ClientInfo = DownloadClientItemClientInfo.FromDownloadClient(provider, false),
                UseSponsorBlock = provider.Settings.UseSponsorBlock,
                SponsorBlockApiEndpoint = provider.Settings.SponsorBlockApiEndpoint,
                TrustedSessionGeneratorUrl = provider.Settings.TrustedSessionGeneratorUrl,
                IsTrack = false,
                ItemId = remoteAlbum.Release.DownloadUrl
            };

            return new YouTubeDownloadRequest(remoteAlbum, options);
        }

        private async Task UpdateClientAsync(YoutubeClient provider)
        {
            if (_sessionToken?.IsValid == true)
                return;
            _sessionToken = await TrustedSessionHelper.GetTrustedSessionTokensAsync(provider.Settings.TrustedSessionGeneratorUrl);
            _youTubeClient = await TrustedSessionHelper.CreateAuthenticatedClientAsync(provider.Settings.TrustedSessionGeneratorUrl, provider.Settings.CookiePath);
        }
    }
}