using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Blocklisting
{
    public abstract class BaseBlocklist<TProtocol> : IBlocklistForProtocol where TProtocol : IDownloadProtocol
    {
        private readonly IBlocklistRepository _blocklistRepository;

        public BaseBlocklist(IBlocklistRepository blocklistRepository) => _blocklistRepository = blocklistRepository;

        public string Protocol => nameof(TProtocol);

        public bool IsBlocklisted(int artistId, ReleaseInfo release) => _blocklistRepository.BlocklistedByTorrentInfoHash(artistId, release.Guid).Any(b => BaseBlocklist<TProtocol>.SameRelease(b, release));

        public Blocklist GetBlocklist(DownloadFailedEvent message) => new()
        {
            ArtistId = message.ArtistId,
            AlbumIds = message.AlbumIds,
            SourceTitle = message.SourceTitle,
            Quality = message.Quality,
            Date = DateTime.UtcNow,
            PublishedDate = DateTime.TryParse(message.Data.GetValueOrDefault("publishedDate") ?? string.Empty, out DateTime publishedDate) ? publishedDate : null,
            Size = long.Parse(message.Data.GetValueOrDefault("size", "0")),
            Indexer = message.Data.GetValueOrDefault("indexer"),
            Protocol = message.Data.GetValueOrDefault("protocol"),
            Message = message.Message,
            TorrentInfoHash = message.Data.GetValueOrDefault("guid")
        };

        private static bool SameRelease(Blocklist item, ReleaseInfo release) => release.Guid.IsNotNullOrWhiteSpace() ? release.Guid.Equals(item.TorrentInfoHash) : item.Indexer.Equals(release.Indexer, StringComparison.InvariantCultureIgnoreCase);
    }
}
