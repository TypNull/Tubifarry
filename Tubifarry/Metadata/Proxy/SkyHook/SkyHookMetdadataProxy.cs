using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.SkyHook;
using NzbDrone.Core.Music;
using NzbDrone.Core.Profiles.Metadata;
using System.Text.RegularExpressions;
using Tubifarry.Metadata.Proxy.Core;
using Tubifarry.Metadata.Proxy.Mixed;

namespace Tubifarry.Metadata.Proxy.SkyHook
{
    [Proxy(ProxyMode.Public)]
    [ProxyFor(typeof(IProvideArtistInfo), 100)]
    [ProxyFor(typeof(IProvideAlbumInfo), 100)]
    [ProxyFor(typeof(ISearchForNewArtist), 100)]
    [ProxyFor(typeof(ISearchForNewAlbum), 100)]
    [ProxyFor(typeof(ISearchForNewEntity), 100)]
    public class SkyHookMetadataProxy : ProxyBase<SykHookMetadataProxySettings>, ISupportMetadataMixing
    {
        private readonly SkyHookProxy _skyHookProxy;

        public override string Name => "Lidarr Default";

        public SkyHookMetadataProxy(
            IHttpClient httpClient,
            IMetadataRequestBuilder requestBuilder,
            IArtistService artistService,
            IAlbumService albumService,
            Logger logger,
            IMetadataProfileService metadataProfileService,
            ICacheManager cacheManager)
        {
            _skyHookProxy = new SkyHookProxy(httpClient, requestBuilder, artistService, albumService, logger, metadataProfileService, cacheManager);
        }

        public Artist GetArtistInfo(string lidarrId, int metadataProfileId) =>
            _skyHookProxy.GetArtistInfo(lidarrId, metadataProfileId);

        public HashSet<string> GetChangedArtists(DateTime startTime) =>
            _skyHookProxy.GetChangedArtists(startTime);

        public Tuple<string, Album, List<ArtistMetadata>> GetAlbumInfo(string id) =>
            _skyHookProxy.GetAlbumInfo(id);

        public HashSet<string> GetChangedAlbums(DateTime startTime) =>
            _skyHookProxy.GetChangedAlbums(startTime);

        public List<Artist> SearchForNewArtist(string title) =>
            _skyHookProxy.SearchForNewArtist(title);

        public List<Album> SearchForNewAlbum(string title, string artist) =>
            _skyHookProxy.SearchForNewAlbum(title, artist);

        public List<Album> SearchForNewAlbumByRecordingIds(List<string> recordingIds) =>
            _skyHookProxy.SearchForNewAlbumByRecordingIds(recordingIds);

        public List<object> SearchForNewEntity(string title) =>
            _skyHookProxy.SearchForNewEntity(title);

        // ISupportMetadataMixing implementation

        /// <summary>
        /// Checks if the given id is in MusicBrainz GUID format (and does not contain an '@').
        /// Returns Supported if valid; otherwise, Unsupported.
        /// </summary>
        public MetadataSupportLevel CanHandleSearch(string? albumTitle = null, string? artistName = null)
        {
            if (albumTitle?.StartsWith("lidarr:") == true || albumTitle?.StartsWith("lidarrid:") == true)
                return MetadataSupportLevel.Supported;

            if ((albumTitle != null && _formatRegex.IsMatch(albumTitle)) || (artistName != null && _formatRegex.IsMatch(artistName)))
                return MetadataSupportLevel.Unsupported;

            return MetadataSupportLevel.Supported;
        }

        public MetadataSupportLevel CanHandleIRecordingIds(params string[] recordingIds) =>
            MetadataSupportLevel.Supported;

        public MetadataSupportLevel CanHandleChanged() =>
            MetadataSupportLevel.Supported;

        public MetadataSupportLevel CanHandleId(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Contains('@'))
                return MetadataSupportLevel.Unsupported;

            if (_guidRegex.IsMatch(id))
                return MetadataSupportLevel.Supported;

            return MetadataSupportLevel.Unsupported;
        }

        /// <summary>
        /// Examines the provided list of links and returns the MusicBrainz GUID if one is found.
        /// Recognizes URLs such as:
        ///   https://musicbrainz.org/artist/xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
        ///   https://musicbrainz.org/release/xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
        ///   https://musicbrainz.org/recording/xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
        /// </summary>
        public string? SupportsLink(List<Links> links)
        {
            if (links == null || links.Count == 0)
                return null;

            foreach (Links link in links)
            {
                if (string.IsNullOrWhiteSpace(link.Url))
                    continue;

                Match match = _musicBrainzRegex.Match(link.Url);
                if (match.Success && match.Groups.Count > 1)
                    return match.Groups[1].Value;
            }
            return null;
        }

        private static readonly Regex _formatRegex = new(@"^\s*\w+:\s*\w+", RegexOptions.Compiled);
        private static readonly Regex _musicBrainzRegex = new(
            @"musicbrainz\.org\/(?:artist|release|recording)\/([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex _guidRegex = new(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}