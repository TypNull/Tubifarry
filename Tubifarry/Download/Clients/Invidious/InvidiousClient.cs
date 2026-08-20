using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;
using System.Text.Json;
using Tubifarry.Core.FFmpeg;
using Tubifarry.Download.Base;
using Tubifarry.Download.Clients.YouTube;
using Tubifarry.Indexers.Invidious;

namespace Tubifarry.Download.Clients.Invidious
{
    public class InvidiousClient(
        IInvidiousDownloadManager downloadManager,
        IConfigService configService,
        IDiskProvider diskProvider,
        INamingConfigService namingConfigService,
        IRemotePathMappingService remotePathMappingService,
        ILocalizationService localizationService,
        IEnumerable<IHttpRequestInterceptor> requestInterceptors,
        IFFmpegInstallation ffmpegInstallation,
        Logger logger) : DownloadClientBase<InvidiousProviderSettings>(configService, diskProvider, remotePathMappingService, localizationService, logger)
    {
        public override string Name => "Invidious";
        public override string Protocol => nameof(YoutubeDownloadProtocol);
        public new InvidiousProviderSettings Settings => base.Settings;

        public override Task<string> Download(RemoteAlbum remoteAlbum, IIndexer indexer) => downloadManager.Download(remoteAlbum, indexer, namingConfigService.GetConfig(), this);

        public override IEnumerable<DownloadClientItem> GetItems() => downloadManager.GetItems();

        public override void RemoveItem(DownloadClientItem item, bool deleteData)
        {
            if (deleteData)
                DeleteItemData(item);
            downloadManager.RemoveItem(item);
        }

        public override DownloadClientInfo GetStatus() => new()
        {
            IsLocalhost = false,
            OutputRootFolders = [new OsPath(Settings.DownloadPath)]
        };

        protected override void Test(List<ValidationFailure> failures)
        {
            if (!_diskProvider.FolderExists(Settings.DownloadPath))
            {
                failures.Add(new ValidationFailure("DownloadPath", "Download path does not exist"));
                return;
            }

            if (!_diskProvider.FolderWritable(Settings.DownloadPath))
            {
                failures.Add(new ValidationFailure("DownloadPath", "Download path is not writable"));
                return;
            }

            try
            {
                BaseHttpClient httpClient = new(Settings.BaseUrl.Trim(), requestInterceptors, TimeSpan.FromSeconds(15));
                string response = httpClient.GetStringAsync("/api/v1/stats").GetAwaiter().GetResult();

                if (string.IsNullOrEmpty(response))
                {
                    failures.Add(new ValidationFailure("BaseUrl", "Cannot connect to Invidious instance: Empty response"));
                    return;
                }

                InvidiousStatsResponse? stats = JsonSerializer.Deserialize<InvidiousStatsResponse>(response, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (stats?.Software?.Name == null || !stats.Software.Name.Equals("invidious", StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add(new ValidationFailure("BaseUrl", "The URL does not appear to be an Invidious instance"));
                    return;
                }

                _logger.Trace($"Successfully connected to Invidious {stats.Software.Version}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error connecting to Invidious instance");
                failures.Add(new ValidationFailure("BaseUrl", $"Cannot connect to Invidious instance: {ex.Message}"));
            }

            ValidationFailure? ffmpegFailure = TestFFmpeg();
            if (ffmpegFailure != null)
                failures.Add(ffmpegFailure);
        }

        private ValidationFailure? TestFFmpeg()
        {
            if (Settings.ReEncode != (int)ReEncodeOptions.Disabled || Settings.UseSponsorBlock)
            {
                if (!ffmpegInstallation.IsInstalled())
                    return new ValidationFailure("ReEncode", "FFmpeg is not available. Set up and test the 'FFmpeg' provider in the Metadata settings to download it, or disable re-encoding and SponsorBlock.");
            }
            return null;
        }
    }
}
