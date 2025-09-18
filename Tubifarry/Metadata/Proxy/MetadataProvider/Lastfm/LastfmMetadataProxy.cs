using NLog;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Music;
using System.Text.RegularExpressions;
using Tubifarry.Metadata.Proxy.MetadataProvider.Mixed;

namespace Tubifarry.Metadata.Proxy.MetadataProvider.Lastfm
{
    [Proxy(ProxyMode.Public)]
    [ProxyFor(typeof(IProvideArtistInfo))]
    [ProxyFor(typeof(IProvideAlbumInfo))]
    [ProxyFor(typeof(ISearchForNewArtist))]
    [ProxyFor(typeof(ISearchForNewAlbum))]
    [ProxyFor(typeof(ISearchForNewEntity))]
    public class LastfmMetadataProxy : ProxyBase<LastfmMetadataProxySettings>, IMetadata, ISupportMetadataMixing
    {
        private readonly ILastfmProxy _lastfmProxy;
        private readonly Logger _logger;

        public override string Name => "Last.fm";
        private LastfmMetadataProxySettings ActiveSettings => Settings ?? LastfmMetadataProxySettings.Instance!;

        public LastfmMetadataProxy(ILastfmProxy lastfmProxy, Logger logger)
        {
            _lastfmProxy = lastfmProxy;
            _logger = logger;
        }

        public List<Album> SearchForNewAlbum(string title, string artist) => _lastfmProxy.SearchNewAlbum(ActiveSettings, title, artist);

        public List<Artist> SearchForNewArtist(string title) => _lastfmProxy.SearchNewArtist(ActiveSettings, title);

        public List<object> SearchForNewEntity(string title) => _lastfmProxy.SearchNewEntity(ActiveSettings, title);

        public Tuple<string, Album, List<ArtistMetadata>> GetAlbumInfo(string foreignAlbumId) => _lastfmProxy.GetAlbumInfoAsync(ActiveSettings, foreignAlbumId).GetAwaiter().GetResult();

        public Artist GetArtistInfo(string lidarrId, int metadataProfileId) => _lastfmProxy.GetArtistInfoAsync(ActiveSettings, lidarrId, metadataProfileId).GetAwaiter().GetResult();

        public HashSet<string> GetChangedAlbums(DateTime startTime)
        {
            _logger.Warn("GetChangedAlbums: Last.fm API does not support change tracking; returning empty set.");
            return new HashSet<string>();
        }

        public HashSet<string> GetChangedArtists(DateTime startTime)
        {
            _logger.Warn("GetChangedArtists: Last.fm API does not support change tracking; returning empty set.");
            return new HashSet<string>();
        }

        public List<Album> SearchForNewAlbumByRecordingIds(List<string> recordingIds)
        {
            _logger.Warn("SearchNewAlbumByRecordingIds: Last.fm API does not support fingerprint search; returning empty list.");
            return new List<Album>();
        }

        public MetadataSupportLevel CanHandleSearch(string? albumTitle, string? artistName)
        {
            if (_lastfmProxy.IsLastfmIdQuery(albumTitle) || _lastfmProxy.IsLastfmIdQuery(artistName))
                return MetadataSupportLevel.Supported;

            if (albumTitle != null && _formatRegex.IsMatch(albumTitle) || artistName != null && _formatRegex.IsMatch(artistName))
                return MetadataSupportLevel.Unsupported;

            return MetadataSupportLevel.ImplicitSupported;
        }

        public MetadataSupportLevel CanHandleId(string id)
        {
            if (id.EndsWith("@lastfm"))
                return MetadataSupportLevel.Supported;
            else
                return MetadataSupportLevel.Unsupported;
        }

        public MetadataSupportLevel CanHandleIRecordingIds(params string[] recordingIds) => MetadataSupportLevel.Unsupported;

        public MetadataSupportLevel CanHandleChanged() => MetadataSupportLevel.Unsupported;

        public string? SupportsLink(List<Links> links)
        {
            if (links == null || links.Count == 0)
                return null;

            foreach (Links link in links)
            {
                if (string.IsNullOrWhiteSpace(link.Url))
                    continue;

                Match match = _lastfmRegex.Match(link.Url);
                if (match.Success && match.Groups.Count > 1)
                {
                    string artistName = match.Groups[1].Value;
                    return $"lastfm:{artistName}";
                }
            }

            return null;
        }

        private static readonly Regex _formatRegex = new(@"^\s*\w+:\s*\w+", RegexOptions.Compiled);
        private static readonly Regex _lastfmRegex = new(@"last\.fm\/music\/([^\/]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}