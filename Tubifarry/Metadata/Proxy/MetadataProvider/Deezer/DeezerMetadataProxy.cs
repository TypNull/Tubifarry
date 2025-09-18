using NLog;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Music;
using System.Text.RegularExpressions;
using Tubifarry.Metadata.Proxy.MetadataProvider.Mixed;

namespace Tubifarry.Metadata.Proxy.MetadataProvider.Deezer
{
    [Proxy(ProxyMode.Public)]
    [ProxyFor(typeof(IProvideArtistInfo))]
    [ProxyFor(typeof(IProvideAlbumInfo))]
    [ProxyFor(typeof(ISearchForNewArtist))]
    [ProxyFor(typeof(ISearchForNewAlbum))]
    [ProxyFor(typeof(ISearchForNewEntity))]
    public class DeezerMetadataProxy : ProxyBase<DeezerMetadataProxySettings>, IMetadata, ISupportMetadataMixing
    {
        private readonly IDeezerProxy _deezerProxy;
        private readonly Logger _logger;

        public override string Name => "Deezer";
        private DeezerMetadataProxySettings ActiveSettings => Settings ?? DeezerMetadataProxySettings.Instance!;

        public DeezerMetadataProxy(IDeezerProxy deezerProxy, Logger logger)
        {
            _deezerProxy = deezerProxy;
            _logger = logger;
        }

        public List<Album> SearchForNewAlbum(string title, string artist) => _deezerProxy.SearchNewAlbum(ActiveSettings, title, artist);

        public List<Artist> SearchForNewArtist(string title) => _deezerProxy.SearchNewArtist(ActiveSettings, title);

        public List<object> SearchForNewEntity(string title) => _deezerProxy.SearchNewEntity(ActiveSettings, title);

        public Tuple<string, Album, List<ArtistMetadata>> GetAlbumInfo(string foreignAlbumId) => _deezerProxy.GetAlbumInfoAsync(ActiveSettings, foreignAlbumId).GetAwaiter().GetResult();

        public Artist GetArtistInfo(string lidarrId, int metadataProfileId) => _deezerProxy.GetArtistInfoAsync(ActiveSettings, lidarrId, metadataProfileId).GetAwaiter().GetResult();

        public HashSet<string> GetChangedAlbums(DateTime startTime)
        {
            _logger.Warn("GetChangedAlbums: Deezer API does not support change tracking; returning empty set.");
            return new HashSet<string>();
        }

        public HashSet<string> GetChangedArtists(DateTime startTime)
        {
            _logger.Warn("GetChangedArtists: Deezer API does not support change tracking; returning empty set.");
            return new HashSet<string>();
        }

        public List<Album> SearchForNewAlbumByRecordingIds(List<string> recordingIds)
        {
            _logger.Warn("SearchNewAlbumByRecordingIds: Deezer API does not support fingerprint search; returning empty list.");
            return new List<Album>();
        }

        public MetadataSupportLevel CanHandleSearch(string? albumTitle, string? artistName)
        {
            if (DeezerProxy.IsDeezerIdQuery(albumTitle) || DeezerProxy.IsDeezerIdQuery(artistName))
                return MetadataSupportLevel.Supported;

            if (albumTitle != null && _formatRegex.IsMatch(albumTitle) || artistName != null && _formatRegex.IsMatch(artistName))
                return MetadataSupportLevel.Unsupported;

            return MetadataSupportLevel.ImplicitSupported;
        }

        public MetadataSupportLevel CanHandleId(string id)
        {
            if (id.EndsWith("@deezer"))
                return MetadataSupportLevel.Supported;
            else
                return MetadataSupportLevel.Unsupported;
        }

        public MetadataSupportLevel CanHandleIRecordingIds(params string[] recordingIds)
        {
            return MetadataSupportLevel.Unsupported;
        }

        public MetadataSupportLevel CanHandleChanged()
        {
            return MetadataSupportLevel.Unsupported;
        }

        public string? SupportsLink(List<Links> links)
        {
            if (links == null || links.Count == 0)
                return null;

            foreach (Links link in links)
            {
                if (string.IsNullOrWhiteSpace(link.Url))
                    continue;

                Match match = _deezerRegex.Match(link.Url);
                if (match.Success && match.Groups.Count > 1)
                    return match.Groups[1].Value;
            }

            return null;
        }

        private static readonly Regex _formatRegex = new(@"^\s*\w+:\s*\w+", RegexOptions.Compiled);
        private static readonly Regex _deezerRegex = new(@"deezer\.com\/(?:album|artist|track)\/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}