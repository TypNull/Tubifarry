using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;
using Requests;
using Tubifarry.Core.Model;
using Tubifarry.Core.Utilities;
using Xabe.FFmpeg;

namespace Tubifarry.Download.Clients.YouTube
{
    public class YoutubeClient : DownloadClientBase<YoutubeProviderSettings>
    {
        private readonly IYoutubeDownloadManager _dlManager;
        private readonly INamingConfigService _naminService;

        public YoutubeClient(IYoutubeDownloadManager dlManager, IConfigService configService, IDiskProvider diskProvider, INamingConfigService namingConfigService, IRemotePathMappingService remotePathMappingService, ILocalizationService localizationService, Logger logger) : base(configService, diskProvider, remotePathMappingService, localizationService, logger)
        {
            _dlManager = dlManager;
            _naminService = namingConfigService;
            RequestHandler.MainRequestHandlers[1].MaxParallelism = 1;
        }

        public override string Name => "Youtube";

        public override string Protocol => nameof(YoutubeDownloadProtocol);

        public new YoutubeProviderSettings Settings => base.Settings;

        public override Task<string> Download(RemoteAlbum remoteAlbum, IIndexer indexer) => _dlManager.Download(remoteAlbum, indexer, _naminService.GetConfig(), this);

        public override IEnumerable<DownloadClientItem> GetItems() => _dlManager.GetItems();

        public override void RemoveItem(DownloadClientItem item, bool deleteData)
        {
            if (deleteData)
                DeleteItemData(item);
            _dlManager.RemoveItem(item);
        }

        public override DownloadClientInfo GetStatus() => new()
        {
            IsLocalhost = false,
            OutputRootFolders = new() { new OsPath(Settings.DownloadPath) }
        };

        protected override void Test(List<ValidationFailure> failures)
        {
            try
            {
                TrustedSessionHelper.ValidateAuthenticationSettingsAsync(Settings.TrustedSessionGeneratorUrl, Settings.CookiePath).Wait();
            }
            catch (Exception ex)
            {
                failures.Add(new ValidationFailure("TrustedSessionGeneratorUrl", $"Error connecting to the trusted session generator: {ex.Message}"));
            }

            _dlManager.SetAuth(Settings);
            if (string.IsNullOrEmpty(Settings.DownloadPath))
                failures.AddRange(PermissionTester.TestAllPermissions(Settings.FFmpegPath, _logger));
            failures.AddIfNotNull(TestFFmpeg().Result);
        }

        public async Task<ValidationFailure> TestFFmpeg()
        {
            if (Settings.ReEncode != (int)ReEncodeOptions.Disabled)
            {
                string old = FFmpeg.ExecutablesPath;
                FFmpeg.SetExecutablesPath(Settings.FFmpegPath);
                AudioMetadataHandler.ResetFFmpegInstallationCheck();
                if (!AudioMetadataHandler.CheckFFmpegInstalled())
                {
                    try
                    {
                        await AudioMetadataHandler.InstallFFmpeg(Settings.FFmpegPath);
                    }
                    catch (Exception ex)
                    {
                        if (!string.IsNullOrEmpty(old))
                            FFmpeg.SetExecutablesPath(old);
                        return new ValidationFailure("FFmpegInstallation", $"Failed to install FFmpeg: {ex.Message}");
                    }
                }
            }
            return null!;
        }
    }
}