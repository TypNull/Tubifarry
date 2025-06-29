using NzbDrone.Core.Music;

namespace Tubifarry.Metadata.Proxy.CustomLidarr
{
    public interface ICustomLidarrProxy
    {
        List<Album> SearchNewAlbum(CustomLidarrMetadataProxySettings settings, string title, string artist);
        List<Artist> SearchNewArtist(CustomLidarrMetadataProxySettings settings, string title);
        List<object> SearchNewEntity(CustomLidarrMetadataProxySettings settings, string query);
        List<Album> SearchNewAlbumByRecordingIds(CustomLidarrMetadataProxySettings settings, List<string> recordingIds);
        Tuple<string, Album, List<ArtistMetadata>> GetAlbumInfo(CustomLidarrMetadataProxySettings settings, string foreignAlbumId);
        Artist GetArtistInfo(CustomLidarrMetadataProxySettings settings, string foreignArtistId, int metadataProfileId);
        HashSet<string> GetChangedAlbums(CustomLidarrMetadataProxySettings settings, DateTime startTime);
        HashSet<string> GetChangedArtists(CustomLidarrMetadataProxySettings settings, DateTime startTime);
        bool IsMbidQuery(string? query);
    }
}