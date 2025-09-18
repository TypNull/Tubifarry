using NLog;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Music;
using System.Text.RegularExpressions;
using Tubifarry.Metadata.Proxy.Core;
using Tubifarry.Metadata.Proxy.Mixed;

namespace Tubifarry.Metadata.Proxy.Discogs
{
    public class DiscogsMetadataProxy : ProxyBase<DiscogsMetadataProxySettings>, IMetadata, ISupportMetadataMixing
    {
        private readonly IDiscogsProxy _discogsProxy;
        private readonly Logger _logger;

        public override string Name => "Discogs";
        private DiscogsMetadataProxySettings ActiveSettings => Settings ?? DiscogsMetadataProxySettings.Instance!;

        public DiscogsMetadataProxy(DiscogsProxy discogsProxy, Logger logger)
        {
            _discogsProxy = discogsProxy;
            _logger = logger;
        }

        public List<Album> SearchForNewAlbum(string title, string artist) => _discogsProxy.SearchNewAlbum(ActiveSettings, title, artist);

        public List<Artist> SearchForNewArtist(string title) => _discogsProxy.SearchNewArtist(ActiveSettings, title);

        public List<object> SearchForNewEntity(string title) => _discogsProxy.SearchNewEntity(ActiveSettings, title);

        public Tuple<string, Album, List<ArtistMetadata>> GetAlbumInfo(string foreignAlbumId) => _discogsProxy.GetAlbumInfoAsync(ActiveSettings, foreignAlbumId).GetAwaiter().GetResult();

        public Artist GetArtistInfo(string lidarrId, int metadataProfileId) => _discogsProxy.GetArtistInfoAsync(ActiveSettings, lidarrId, metadataProfileId).GetAwaiter().GetResult();

        public HashSet<string> GetChangedAlbums(DateTime startTime)
        {
            _logger.Warn("GetChangedAlbums: Discogs API does not support change tracking; returning empty set.");
            return new HashSet<string>();
        }

        public HashSet<string> GetChangedArtists(DateTime startTime)
        {
            _logger.Warn("GetChangedArtists: Discogs API does not support change tracking; returning empty set.");
            return new HashSet<string>();
        }

        public List<Album> SearchForNewAlbumByRecordingIds(List<string> recordingIds)
        {
            _logger.Warn("SearchNewAlbumByRecordingIds: Discogs API does not support fingerprint search; returning empty list.");
            return new List<Album>();
        }

        public MetadataSupportLevel CanHandleSearch(string? albumTitle, string? artistName)
        {
            if (_discogsProxy.IsDiscogsidQuery(albumTitle) || _discogsProxy.IsDiscogsidQuery(artistName))
                return MetadataSupportLevel.Supported;

            if (albumTitle != null && _formatRegex.IsMatch(albumTitle) || artistName != null && _formatRegex.IsMatch(artistName))
                return MetadataSupportLevel.Unsupported;

            return MetadataSupportLevel.ImplicitSupported;
        }

        public MetadataSupportLevel CanHandleId(string id)
        {
            if (id.EndsWith("@discogs"))
                return MetadataSupportLevel.Supported;
            else return MetadataSupportLevel.Unsupported;
        }

        public MetadataSupportLevel CanHandleIRecordingIds(params string[] recordingIds) =>
            MetadataSupportLevel.Unsupported;

        public MetadataSupportLevel CanHandleChanged() => MetadataSupportLevel.Unsupported;

        public string? SupportsLink(List<Links> links)
        {
            if (links == null || links.Count == 0)
                return null;

            foreach (Links link in links)
            {
                if (string.IsNullOrWhiteSpace(link.Url))
                    continue;

                Match match = _discogsRegex.Match(link.Url);
                if (match.Success && match.Groups.Count > 1)
                    return match.Groups[1].Value;
            }
            return null;
        }

        private static readonly Regex _formatRegex = new(@"^\s*\w+:\s*\w+", RegexOptions.Compiled);
        private static readonly Regex _discogsRegex = new(@"discogs\.com\/(?:artist|release|master)\/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}