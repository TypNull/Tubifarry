using DryIoc;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Music;

namespace Tubifarry.Metadata
{
    public interface IProxyProvideArtistInfo
    {
        Artist GetArtistInfo(string lidarrId, int metadataProfileId);
        HashSet<string> GetChangedArtists(DateTime startTime);
    }

    public interface IProxySearchForNewArtist
    {
        List<Artist> SearchForNewArtist(string title);
    }

    public interface IProxyProvideAlbumInfo
    {
        Tuple<string, Album, List<ArtistMetadata>> GetAlbumInfo(string id);
        HashSet<string> GetChangedAlbums(DateTime startTime);
    }

    public interface IProxySearchForNewAlbum
    {
        List<Album> SearchForNewAlbum(string title, string artist);
        List<Album> SearchForNewAlbumByRecordingIds(List<string> recordingIds);
    }

    public interface IProxySearchForNewEntity
    {
        List<object> SearchForNewEntity(string title);
    }


    public class ProxyForMetadataProxy : IProvideArtistInfo, ISearchForNewArtist, IProvideAlbumInfo, ISearchForNewAlbum, ISearchForNewEntity
    {
        private readonly IContainer _container;

        public ProxyForMetadataProxy(IContainer container) => _container = container;

        public Artist GetArtistInfo(string lidarrId, int metadataProfileId) =>
            _container.Resolve<IProxyProvideArtistInfo>().GetArtistInfo(lidarrId, metadataProfileId);

        public HashSet<string> GetChangedArtists(DateTime startTime) =>
            _container.Resolve<IProxyProvideArtistInfo>().GetChangedArtists(startTime);

        public List<Artist> SearchForNewArtist(string title) =>
            _container.Resolve<IProxySearchForNewArtist>().SearchForNewArtist(title);

        public Tuple<string, Album, List<ArtistMetadata>> GetAlbumInfo(string id) =>
            _container.Resolve<IProxyProvideAlbumInfo>().GetAlbumInfo(id);

        public HashSet<string> GetChangedAlbums(DateTime startTime) =>
            _container.Resolve<IProxyProvideAlbumInfo>().GetChangedAlbums(startTime);

        public List<Album> SearchForNewAlbum(string title, string artist) =>
            _container.Resolve<IProxySearchForNewAlbum>().SearchForNewAlbum(title, artist);

        public List<Album> SearchForNewAlbumByRecordingIds(List<string> recordingIds) =>
            _container.Resolve<IProxySearchForNewAlbum>().SearchForNewAlbumByRecordingIds(recordingIds);

        public List<object> SearchForNewEntity(string title) =>
            _container.Resolve<IProxySearchForNewEntity>().SearchForNewEntity(title);
    }
}
