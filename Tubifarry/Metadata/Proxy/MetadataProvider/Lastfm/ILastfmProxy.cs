using NzbDrone.Core.Music;

namespace Tubifarry.Metadata.Proxy.MetadataProvider.Lastfm
{
    public interface ILastfmProxy
    {
        List<Album> SearchNewAlbum(LastfmMetadataProxySettings settings, string title, string artist);

        List<Artist> SearchNewArtist(LastfmMetadataProxySettings settings, string title);

        List<object> SearchNewEntity(LastfmMetadataProxySettings settings, string query);

        Task<Tuple<string, Album, List<ArtistMetadata>>> GetAlbumInfoAsync(LastfmMetadataProxySettings settings, string foreignAlbumId);

        Task<Artist> GetArtistInfoAsync(LastfmMetadataProxySettings settings, string foreignArtistId, int metadataProfileId);

        bool IsLastfmIdQuery(string? query);
    }
}