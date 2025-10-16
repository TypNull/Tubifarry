using NzbDrone.Common.Http;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Music;

namespace Tubifarry.Metadata.Proxy.MetadataProvider
{
    public class MetadataProviderWrapper(Lazy<IProxyService> proxyService) : ProxyWrapperBase(proxyService), IProvideArtistInfo, IProvideAlbumInfo, ISearchForNewArtist, ISearchForNewAlbum, ISearchForNewEntity
    {
        // IProvideArtistInfo implementation
        public Artist GetArtistInfo(string lidarrId, int metadataProfileId) =>
            InvokeProxyMethod<Artist>(
                typeof(IProvideArtistInfo),
                nameof(GetArtistInfo),
                lidarrId, metadataProfileId);

        public HashSet<string> GetChangedArtists(DateTime startTime) =>
            InvokeProxyMethod<HashSet<string>>(
                typeof(IProvideArtistInfo),
                nameof(GetChangedArtists),
                startTime);

        // IProvideAlbumInfo implementation
        public Tuple<string, Album, List<ArtistMetadata>> GetAlbumInfo(string id) =>
            InvokeProxyMethod<Tuple<string, Album, List<ArtistMetadata>>>(
                typeof(IProvideAlbumInfo),
                nameof(GetAlbumInfo),
                id);

        public HashSet<string> GetChangedAlbums(DateTime startTime) =>
            InvokeProxyMethod<HashSet<string>>(
                typeof(IProvideAlbumInfo),
                nameof(GetChangedAlbums),
                startTime);

        // ISearchForNewArtist implementation
        public List<Artist> SearchForNewArtist(string title) =>
            InvokeProxyMethod<List<Artist>>(
                typeof(ISearchForNewArtist),
                nameof(SearchForNewArtist),
                title);

        // ISearchForNewAlbum implementation
        public List<Album> SearchForNewAlbum(string title, string artist) =>
            InvokeProxyMethod<List<Album>>(
                typeof(ISearchForNewAlbum),
                nameof(SearchForNewAlbum),
                title, artist);

        public List<Album> SearchForNewAlbumByRecordingIds(List<string> recordingIds) =>
            InvokeProxyMethod<List<Album>>(
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

    public class MetadataRequestBuilderWrapper(Lazy<IProxyService> proxyService) : ProxyWrapperBase(proxyService), IMetadataRequestBuilder
    {
        public IHttpRequestBuilderFactory GetRequestBuilder() =>
           InvokeProxyMethod<IHttpRequestBuilderFactory>(
               typeof(IMetadataRequestBuilder),
               nameof(GetRequestBuilder));
    }
}