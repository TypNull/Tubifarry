using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;

namespace Tubifarry.Download.Clients.Lucida
{
    /// <summary>
    /// Lucida download client for high-quality music downloads
    /// Integrates with Lucida web interface to download tracks and albums
    /// </summary>
    public class LucidaClient : DownloadClientBase<LucidaProviderSettings>
    {
        private readonly ILucidaDownloadManager _downloadManager;
        private readonly INamingConfigService _namingService;

        public LucidaClient(
            ILucidaDownloadManager downloadManager,
            IConfigService configService,
            IDiskProvider diskProvider,
            INamingConfigService namingConfigService,
            IRemotePathMappingService remotePathMappingService,
            ILocalizationService localizationService,
            Logger logger)
            : base(configService, diskProvider, remotePathMappingService, localizationService, logger)
        {
            _downloadManager = downloadManager;
            _namingService = namingConfigService;
        }

        public override string Name => "Lucida";
        public override string Protocol => nameof(LucidaDownloadProtocol);
        public new LucidaProviderSettings Settings => base.Settings;

        public override Task<string> Download(RemoteAlbum remoteAlbum, IIndexer indexer) => _downloadManager.Download(remoteAlbum, indexer, _namingService.GetConfig(), this);

        public override IEnumerable<DownloadClientItem> GetItems() => _downloadManager.GetItems();

        public override void RemoveItem(DownloadClientItem item, bool deleteData)
        {
            if (deleteData)
                DeleteItemData(item);
            _downloadManager.RemoveItem(item);
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
        }
    }
}
