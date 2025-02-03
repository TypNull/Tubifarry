using FluentValidation.Results;
using NLog;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Music;

namespace Tubifarry.Metadata.Proxy.DiscogsProxy
{
    public class DiscogsMetadataProxy : ConsumerProxyPlaceholder<DiscogsMetadataProxySettings>, IMetadata
    {
        private readonly IDiscogsProxy _discogsProxy;
        private readonly Logger _logger;

        public override string Name => "Discogs";
        private DiscogsMetadataProxySettings ActiveSettings => Settings ?? DiscogsMetadataProxySettings.Instance!;

        public DiscogsMetadataProxy(Lazy<IProxyService> proxyService, IDiscogsProxy discogsProxy, Logger logger) : base(proxyService)
        {
            _discogsProxy = discogsProxy;
            _logger = logger;
            _logger.Info("DiscogsMetadataProxy initialized.");
        }

        public override ValidationResult Test() => _discogsProxy.Test(ActiveSettings);

        public override List<Album> SearchForNewAlbum(string title, string artist) => _discogsProxy.SearchNewAlbum(ActiveSettings, title, artist);

        public override List<Artist> SearchForNewArtist(string title) => _discogsProxy.SearchNewArtist(ActiveSettings, title);

        public override List<object> SearchForNewEntity(string title) => _discogsProxy.SearchNewEntity(ActiveSettings, title);

        public override Tuple<string, Album, List<ArtistMetadata>> GetAlbumInfo(string foreignAlbumId) => _discogsProxy.GetAlbumInfo(ActiveSettings, foreignAlbumId);

        public override HashSet<string> GetChangedAlbums(DateTime startTime) => _discogsProxy.GetChangedAlbums(ActiveSettings, startTime);

        public override HashSet<string> GetChangedArtists(DateTime startTime) => _discogsProxy.GetChangedArtists(ActiveSettings, startTime);

        public override List<Album> SearchForNewAlbumByRecordingIds(List<string> recordingIds) => _discogsProxy.SearchNewAlbumByRecordingIds(ActiveSettings, recordingIds);

        public override Artist GetArtistInfo(string lidarrId, int metadataProfileId) => _discogsProxy.GetArtistInfo(ActiveSettings, lidarrId, metadataProfileId);
    }
}