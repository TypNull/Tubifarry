using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Indexers;

namespace Tubifarry.Blocklisting
{
    public class YoutubeBlocklist : BaseBlocklist<YoutubeDownloadProtocol>
    {
        public YoutubeBlocklist(IBlocklistRepository blocklistRepository) : base(blocklistRepository) { }

    }

    public class SoulseekBlocklist : BaseBlocklist<SoulseekDownloadProtocol>
    {
        public SoulseekBlocklist(IBlocklistRepository blocklistRepository) : base(blocklistRepository) { }
    }
}
