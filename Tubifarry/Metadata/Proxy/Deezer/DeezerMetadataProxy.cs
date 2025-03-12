using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Music;
using System.Text.RegularExpressions;
using Tubifarry.Metadata.Proxy.Core;
using Tubifarry.Metadata.Proxy.Mixed;

namespace Tubifarry.Metadata.Proxy.Deezer
{
    public class DeezerMetadataProxy : ConsumerProxyPlaceholder<DeezerMetadataProxySettings>, IMetadata, ISupportMetadataMixing
    {
        private readonly IDeezerProxy _deezerProxy;
        private readonly Logger _logger;

        public override string Name => "Deezer";
        private DeezerMetadataProxySettings ActiveSettings => Settings ?? DeezerMetadataProxySettings.Instance!;

        public DeezerMetadataProxy(Lazy<IProxyService> proxyService, IHttpClient client, IDeezerProxy deezerProxy, Logger logger) : base(proxyService)
        {
            _deezerProxy = deezerProxy;
            _logger = logger;
            _httpClient = client;
        }

        private readonly IHttpClient _httpClient;
        public override ValidationResult Test()
        {
            return new();
        }

        public override List<Album> SearchForNewAlbum(string title, string artist) => _deezerProxy.SearchNewAlbum(ActiveSettings, title, artist);

        public override List<Artist> SearchForNewArtist(string title) => _deezerProxy.SearchNewArtist(ActiveSettings, title);

        public override List<object> SearchForNewEntity(string title) => _deezerProxy.SearchNewEntity(ActiveSettings, title);

        public override Tuple<string, Album, List<ArtistMetadata>> GetAlbumInfo(string foreignAlbumId) => _deezerProxy.GetAlbumInfoAsync(ActiveSettings, foreignAlbumId).GetAwaiter().GetResult();

        public override Artist GetArtistInfo(string lidarrId, int metadataProfileId) => _deezerProxy.GetArtistInfoAsync(ActiveSettings, lidarrId, metadataProfileId).GetAwaiter().GetResult();

        public override HashSet<string> GetChangedAlbums(DateTime startTime)
        {
            _logger.Warn("GetChangedAlbums: Deezer API does not support change tracking; returning empty set.");
            return new HashSet<string>();
        }

        public override HashSet<string> GetChangedArtists(DateTime startTime)
        {
            _logger.Warn("GetChangedArtists: Deezer API does not support change tracking; returning empty set.");
            return new HashSet<string>();
        }

        public override List<Album> SearchForNewAlbumByRecordingIds(List<string> recordingIds)
        {
            _logger.Warn("SearchNewAlbumByRecordingIds: Deezer API does not support fingerprint search; returning empty list.");
            return new List<Album>();
        }

        public MetadataSupportLevel CanHandleSearch(string? albumTitle, string? artistName)
        {
            if (DeezerProxy.IsDeezerIdQuery(albumTitle) || DeezerProxy.IsDeezerIdQuery(artistName))
                return MetadataSupportLevel.Supported;

            Regex regex = new(@"^\s*\w+:");

            if ((albumTitle != null && regex.IsMatch(albumTitle)) || (artistName != null && regex.IsMatch(artistName)))
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

            Regex deezerRegex = new(@"deezer\.com\/(?:album|artist|track)\/(\d+)", RegexOptions.IgnoreCase);

            foreach (Links link in links)
            {
                if (string.IsNullOrWhiteSpace(link.Url))
                    continue;

                Match match = deezerRegex.Match(link.Url);
                if (match.Success && match.Groups.Count > 1)
                    return match.Groups[1].Value;
            }

            return null;
        }
    }
}
