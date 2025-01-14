using NLog;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.ThingiProvider;

namespace Tubifarry.Proxy
{
    public interface IProxyRepository : IProviderRepository<ProxyDefinition> { }

    public class ProxyRepository : ProviderRepository<ProxyDefinition>, IProxyRepository
    {
        public ProxyRepository(IMainDatabase database, IEventAggregator eventAggregator, Logger logger) : base(database, eventAggregator, logger) { }
    }

    /// <summary>
    /// Utilizes the metadata table to avoid potential issues that may arise from directly modifying the database. This approach achieves the same result, as the previous method was also ineffective. // Did not work
    /// First, migrate the database, then restart the application and add the table to the MetadataDefinition in the table mapper.
    /// </summary>
    public class ProxyDefinition : ProviderDefinition { }

}
