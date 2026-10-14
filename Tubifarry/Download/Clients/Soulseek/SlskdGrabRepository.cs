using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Messaging.Events;
using System.Linq.Expressions;

namespace Tubifarry.Download.Clients.Soulseek;

public sealed class SlskdGrabRepository(IMainDatabase database, IEventAggregator eventAggregator)
    : BasicRepository<DownloadHistory>(database, eventAggregator)
{
    public List<DownloadHistory> GetGrabs(int definitionId, Expression<Func<DownloadHistory, bool>> filter, int limit) =>
        Query(Builder()
            .Where<DownloadHistory>(h => h.EventType == DownloadHistoryEventType.DownloadGrabbed)
            .Where<DownloadHistory>(h => h.DownloadClientId == definitionId)
            .Where(filter)
            .OrderBy($@"""DownloadHistory"".""Id"" DESC LIMIT {limit}"));
}
