using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Indexers;

namespace Tubifarry.Blocklisting
{
    public class YoutubeBlocklist(IBlocklistRepository blocklistRepository) : BaseBlocklist<YoutubeDownloadProtocol>(blocklistRepository)
    { }

    public class SoulseekBlocklist(IBlocklistRepository blocklistRepository) : BaseBlocklist<SoulseekDownloadProtocol>(blocklistRepository)
    { }
}
