using FluentValidation.Results;
using NLog;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Music;

namespace Tubifarry.Metadata.Proxy.Mixed
{
    public interface IMixedProxy : IProxy { }
    public class MixedMetadataProxy : ConsumerProxyPlaceholder<MixedMetadataProxySettings>, IMetadata, IMixedProxy
    {
        public override string Name => "MetaMix";
        private readonly Logger _logger;

        public MixedMetadataProxy(Lazy<IProxyService> proxyService, Logger logger) : base(proxyService)
        {
            _logger = logger;
        }

        public override ValidationResult Test()
        {
            _logger.Info("Test");
            return new();
        }

        public override Tuple<string, Album, List<ArtistMetadata>> GetAlbumInfo(string id)
        {
            throw new NotImplementedException();
        }

        public override Artist GetArtistInfo(string lidarrId, int metadataProfileId)
        {
            throw new NotImplementedException();
        }

        public override HashSet<string> GetChangedAlbums(DateTime startTime)
        {
            throw new NotImplementedException();
        }

        public override HashSet<string> GetChangedArtists(DateTime startTime)
        {
            throw new NotImplementedException();
        }

        public override List<Album> SearchForNewAlbum(string title, string artist)
        {
            throw new NotImplementedException();
        }

        public override List<Album> SearchForNewAlbumByRecordingIds(List<string> recordingIds)
        {
            throw new NotImplementedException();
        }

        public override List<Artist> SearchForNewArtist(string title)
        {
            throw new NotImplementedException();
        }

        public override List<object> SearchForNewEntity(string title)
        {
            throw new NotImplementedException();
        }
    }
}
