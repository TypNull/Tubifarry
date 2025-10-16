using NzbDrone.Common.Cloud;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Music;
using System.Text.RegularExpressions;
using Tubifarry.Metadata.Proxy.MetadataProvider.Mixed;

namespace Tubifarry.Metadata.Proxy.MetadataProvider.CustomLidarr
{
    [Proxy(ProxyMode.Public)]
    [ProxyFor(typeof(IProvideArtistInfo))]
    [ProxyFor(typeof(IProvideAlbumInfo))]
    [ProxyFor(typeof(ISearchForNewArtist))]
    [ProxyFor(typeof(ISearchForNewAlbum))]
    [ProxyFor(typeof(ISearchForNewEntity))]
    [ProxyFor(typeof(IMetadataRequestBuilder))]
    public class CustomLidarrMetadataProxy : ProxyBase<CustomLidarrMetadataProxySettings>, IMetadata, ISupportMetadataMixing
    {
        private readonly ICustomLidarrProxy _customLidarrProxy;
        private readonly IConfigService _configService;
        private readonly ILidarrCloudRequestBuilder _defaultRequestFactory;

        public override string Name => "Lidarr Custom";
        private static CustomLidarrMetadataProxySettings ActiveSettings => CustomLidarrMetadataProxySettings.Instance!;

        public CustomLidarrMetadataProxy(IConfigService configService, ILidarrCloudRequestBuilder defaultRequestBuilder, ICustomLidarrProxy customLidarrProxy)
        {
            _customLidarrProxy = customLidarrProxy;
            _configService = configService;
            _defaultRequestFactory = defaultRequestBuilder;
        }

        public List<Album> SearchForNewAlbum(string title, string artist) =>
            _customLidarrProxy.SearchNewAlbum(ActiveSettings, title, artist);

        public List<Artist> SearchForNewArtist(string title) =>
            _customLidarrProxy.SearchNewArtist(ActiveSettings, title);

        public List<object> SearchForNewEntity(string title) =>
            _customLidarrProxy.SearchNewEntity(ActiveSettings, title);

        public Tuple<string, Album, List<ArtistMetadata>> GetAlbumInfo(string foreignAlbumId) =>
            _customLidarrProxy.GetAlbumInfo(ActiveSettings, foreignAlbumId);

        public Artist GetArtistInfo(string lidarrId, int metadataProfileId) =>
            _customLidarrProxy.GetArtistInfo(ActiveSettings, lidarrId, metadataProfileId);

        public HashSet<string> GetChangedAlbums(DateTime startTime) =>
            _customLidarrProxy.GetChangedAlbums(ActiveSettings, startTime);

        public HashSet<string> GetChangedArtists(DateTime startTime) =>
            _customLidarrProxy.GetChangedArtists(ActiveSettings, startTime);

        public List<Album> SearchForNewAlbumByRecordingIds(List<string> recordingIds) =>
            _customLidarrProxy.SearchNewAlbumByRecordingIds(ActiveSettings, recordingIds);

        public IHttpRequestBuilderFactory GetRequestBuilder()
        {
            if (Settings?.MetadataSource?.IsNotNullOrWhiteSpace() == true && ActiveSettings.CanProxySpotify)
            {
                return new HttpRequestBuilder(Settings?.MetadataSource.TrimEnd("/") + "/{route}").KeepAlive().CreateFactory();
            }
            else if (_configService.MetadataSource.IsNotNullOrWhiteSpace())
            {
                return new HttpRequestBuilder(_configService.MetadataSource.TrimEnd("/") + "/{route}").KeepAlive().CreateFactory();
            }
            else
            {
                return _defaultRequestFactory.Search;
            }
        }

        public MetadataSupportLevel CanHandleSearch(string? albumTitle, string? artistName)
        {
            if (albumTitle?.StartsWith("cl:") == true || albumTitle?.StartsWith("clid:") == true || albumTitle?.StartsWith("customlidarrid:") == true)
                return MetadataSupportLevel.Supported;

            if (albumTitle != null && _formatRegex.IsMatch(albumTitle) || (artistName != null && _formatRegex.IsMatch(artistName)))
                return MetadataSupportLevel.Unsupported;

            return MetadataSupportLevel.Supported;
        }

        public MetadataSupportLevel CanHandleIRecordingIds(params string[] recordingIds)
        {
            return MetadataSupportLevel.Supported;
        }

        public MetadataSupportLevel CanHandleChanged()
        {
            return MetadataSupportLevel.Supported;
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

        /// <summary>
        /// Checks if the given id is in MusicBrainz GUID format (and does not contain an '@').
        /// Returns Supported if valid; otherwise, Unsupported.
        /// </summary>
        public MetadataSupportLevel CanHandleId(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Contains('@'))
                return MetadataSupportLevel.Unsupported;

            if (_guidRegex.IsMatch(id))
                return MetadataSupportLevel.Supported;

            return MetadataSupportLevel.Unsupported;
        }

        private static readonly Regex _formatRegex = new(@"^\s*\w+:\s*\w+", RegexOptions.Compiled);
        private static readonly Regex _musicBrainzRegex = new(
            @"musicbrainz\.org\/(?:artist|release|recording)\/([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex _guidRegex = new("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}