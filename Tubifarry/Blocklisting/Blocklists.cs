using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Indexers;

namespace Tubifarry.Blocklisting
{
    public class YoutubeBlocklist(IBlocklistRepository blocklistRepository) : BaseBlocklist<YoutubeDownloadProtocol>(blocklistRepository)
    { }

    public class SoulseekBlocklist(IBlocklistRepository blocklistRepository) : BaseBlocklist<SoulseekDownloadProtocol>(blocklistRepository)
    { }

    public class QobuzBlocklist(IBlocklistRepository blocklistRepository) : BaseBlocklist<QobuzDownloadProtocol>(blocklistRepository)
    { }

    public class LucidaBlocklist(IBlocklistRepository blocklistRepository) : BaseBlocklist<LucidaDownloadProtocol>(blocklistRepository)
    { }

    public class SubSonicBlocklist(IBlocklistRepository blocklistRepository) : BaseBlocklist<SubSonicDownloadProtocol>(blocklistRepository)
    { }
}