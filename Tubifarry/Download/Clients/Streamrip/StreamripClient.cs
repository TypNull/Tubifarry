using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;
using Tubifarry.Indexers.Streamrip;

namespace Tubifarry.Download.Clients.Streamrip
{
    public class StreamripClient(IStreamripDownloadManager downloads, IConfigService configService, IDiskProvider diskProvider, IRemotePathMappingService remotePathMappingService, ILocalizationService localizationService, Logger logger)
        : DownloadClientBase<StreamripClientSettings>(configService, diskProvider, remotePathMappingService, localizationService, logger)
    {
        public override string Name => "Streamrip";
        public override string Protocol => nameof(StreamripDownloadProtocol);

        public override Task<string> Download(RemoteAlbum remoteAlbum, IIndexer indexer)
        {
            if (indexer?.Definition?.Settings is not StreamripIndexerSettings source)
                throw new DownloadClientException($"Streamrip can only download releases of a Streamrip indexer, not '{indexer?.Name}'");

            string id = downloads.Enqueue(new StreamripJobRequest(remoteAlbum, source, Settings, Definition.Id, DownloadClientItemClientInfo.FromDownloadClient(this, false)));
            return Task.FromResult(id);
        }

        public override IEnumerable<DownloadClientItem> GetItems() => downloads.GetItems(Definition.Id);

        public override void RemoveItem(DownloadClientItem item, bool deleteData)
        {
            downloads.Remove(item.DownloadId);
            if (deleteData)
                DeleteItemData(item);
        }

        public override DownloadClientInfo GetStatus() => new()
        {
            IsLocalhost = true,
            OutputRootFolders = [new OsPath(Settings.DownloadPath)]
        };

        protected override void Test(List<ValidationFailure> failures)
        {
            ValidationFailure? folder = TestFolder(Settings.DownloadPath, nameof(Settings.DownloadPath));
            if (folder != null)
                failures.Add(folder);
        }
    }
}
