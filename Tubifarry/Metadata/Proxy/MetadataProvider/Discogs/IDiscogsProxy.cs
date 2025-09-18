using NzbDrone.Core.Music;

namespace Tubifarry.Metadata.Proxy.MetadataProvider.Discogs
{
    public interface IDiscogsProxy
    {
        List<Album> SearchNewAlbum(DiscogsMetadataProxySettings settings, string title, string artist);
        List<Artist> SearchNewArtist(DiscogsMetadataProxySettings settings, string title);
        List<object> SearchNewEntity(DiscogsMetadataProxySettings settings, string title);
        Task<Tuple<string, Album, List<ArtistMetadata>>> GetAlbumInfoAsync(DiscogsMetadataProxySettings settings, string foreignAlbumId);
        Task<Artist> GetArtistInfoAsync(DiscogsMetadataProxySettings settings, string lidarrId, int metadataProfileId);
        bool IsDiscogsidQuery(string? artistName);
    }
}
