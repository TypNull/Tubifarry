using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;

namespace Tubifarry.Download.Clients.Soulseek;

public class SlskdClient : DownloadClientBase<SlskdProviderSettings>
{
    private readonly ISlskdDownloadManager _manager;
    private readonly ISlskdApiClient _apiClient;

    public override string Name => "Slskd";
    public override string Protocol => nameof(SoulseekDownloadProtocol);

    public SlskdClient(
        ISlskdDownloadManager manager,
        ISlskdApiClient apiClient,
        IConfigService configService,
        IDiskProvider diskProvider,
        IRemotePathMappingService remotePathMappingService,
        ILocalizationService localizationService,
        Logger logger)
        : base(configService, diskProvider, remotePathMappingService, localizationService, logger)
    {
        _manager = manager;
        _apiClient = apiClient;
    }

    public override async Task<string> Download(RemoteAlbum remoteAlbum, IIndexer indexer) =>
        await _manager.DownloadAsync(remoteAlbum, Definition.Id, Settings);

    public override IEnumerable<DownloadClientItem> GetItems()
    {
        DownloadClientItemClientInfo clientInfo = DownloadClientItemClientInfo.FromDownloadClient(this, false);
        foreach (DownloadClientItem item in _manager.GetItems(Definition.Id, Settings, GetRemoteToLocal()))
        {
            item.DownloadClientInfo = clientInfo;
            yield return item;
        }
    }

    public override void RemoveItem(DownloadClientItem clientItem, bool deleteData) =>
        _manager.RemoveItem(clientItem, deleteData, Definition.Id, Settings);

    public override DownloadClientInfo GetStatus() => new()
    {
        IsLocalhost = Settings.IsLocalhost,
        OutputRootFolders = [_remotePathMappingService.RemapRemoteToLocal(Settings.Host, new OsPath(Settings.DownloadPath))]
    };

    protected override void Test(List<ValidationFailure> failures)
    {
        if (_apiClient.TestConnectionAsync(Settings).GetAwaiter().GetResult() is ValidationFailure failure)
            failures.Add(failure);
    }

    private OsPath GetRemoteToLocal() =>
        _remotePathMappingService.RemapRemoteToLocal(Settings.Host, new OsPath(Settings.DownloadPath));
}
