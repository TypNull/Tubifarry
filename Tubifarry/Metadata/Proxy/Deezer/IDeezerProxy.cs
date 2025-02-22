using NzbDrone.Core.Music;

namespace Tubifarry.Metadata.Proxy.Deezer
{
    public interface IDeezerProxy
    {
        List<Album> SearchNewAlbum(DeezerMetadataProxySettings settings, string title, string artist);
        List<Artist> SearchNewArtist(DeezerMetadataProxySettings settings, string title);
        List<object> SearchNewEntity(DeezerMetadataProxySettings settings, string query);
        Task<Tuple<string, Album, List<ArtistMetadata>>> GetAlbumInfoAsync(DeezerMetadataProxySettings settings, string foreignAlbumId);
        Task<Artist> GetArtistInfoAsync(DeezerMetadataProxySettings settings, string foreignArtistId, int metadataProfileId);
    }
}
