using NzbDrone.Core.MetadataSource;

namespace Tubifarry.Metadata.Proxy.Core
{
    public class MetadataWrapper : ProxyWrapperBase, IProvideArtistInfo, IProvideAlbumInfo, ISearchForNewArtist, ISearchForNewAlbum, ISearchForNewEntity
    {
        public MetadataWrapper(Lazy<IProxyService> proxyService) : base(proxyService) { }

        // IProvideArtistInfo implementation
        public NzbDrone.Core.Music.Artist GetArtistInfo(string lidarrId, int metadataProfileId) =>
            InvokeProxyMethod<NzbDrone.Core.Music.Artist>(
                typeof(IProvideArtistInfo),
                nameof(GetArtistInfo),
                lidarrId, metadataProfileId);

        public HashSet<string> GetChangedArtists(DateTime startTime) =>
            InvokeProxyMethod<HashSet<string>>(
                typeof(IProvideArtistInfo),
                nameof(GetChangedArtists),
                startTime);

        // IProvideAlbumInfo implementation
        public Tuple<string, NzbDrone.Core.Music.Album, List<NzbDrone.Core.Music.ArtistMetadata>> GetAlbumInfo(string id) =>
            InvokeProxyMethod<Tuple<string, NzbDrone.Core.Music.Album, List<NzbDrone.Core.Music.ArtistMetadata>>>(
                typeof(IProvideAlbumInfo),
                nameof(GetAlbumInfo),
                id);

        public HashSet<string> GetChangedAlbums(DateTime startTime) =>
            InvokeProxyMethod<HashSet<string>>(
                typeof(IProvideAlbumInfo),
                nameof(GetChangedAlbums),
                startTime);

        // ISearchForNewArtist implementation
        public List<NzbDrone.Core.Music.Artist> SearchForNewArtist(string title) =>
            InvokeProxyMethod<List<NzbDrone.Core.Music.Artist>>(
                typeof(ISearchForNewArtist),
                nameof(SearchForNewArtist),
                title);

        // ISearchForNewAlbum implementation
        public List<NzbDrone.Core.Music.Album> SearchForNewAlbum(string title, string artist) =>
            InvokeProxyMethod<List<NzbDrone.Core.Music.Album>>(
                typeof(ISearchForNewAlbum),
                nameof(SearchForNewAlbum),
                title, artist);

        public List<NzbDrone.Core.Music.Album> SearchForNewAlbumByRecordingIds(List<string> recordingIds) =>
            InvokeProxyMethod<List<NzbDrone.Core.Music.Album>>(
                typeof(ISearchForNewAlbum),
                nameof(SearchForNewAlbumByRecordingIds),
                recordingIds);

        // ISearchForNewEntity implementation
        public List<object> SearchForNewEntity(string title) =>
            InvokeProxyMethod<List<object>>(
                typeof(ISearchForNewEntity),
                nameof(SearchForNewEntity),
                title);
    }
}