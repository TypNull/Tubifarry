using System;
using System.Collections.Generic;
using FluentValidation.Results;
using NzbDrone.Core.Music;
using NzbDrone.Core.Extras.Metadata;

namespace Tubifarry.Metadata.Proxy.DiscogsProxy
{
    public interface IDiscogsProxy
    {
        ValidationResult Test(DiscogsMetadataProxySettings settings);
        List<Album> SearchNewAlbum(DiscogsMetadataProxySettings settings, string title, string artist);
        List<Artist> SearchNewArtist(DiscogsMetadataProxySettings settings, string title);
        List<object> SearchNewEntity(DiscogsMetadataProxySettings settings, string title);
        Tuple<string, Album, List<ArtistMetadata>> GetAlbumInfo(DiscogsMetadataProxySettings settings, string foreignAlbumId);
        HashSet<string> GetChangedAlbums(DiscogsMetadataProxySettings settings, DateTime startTime);
        HashSet<string> GetChangedArtists(DiscogsMetadataProxySettings settings, DateTime startTime);
        List<Album> SearchNewAlbumByRecordingIds(DiscogsMetadataProxySettings settings, List<string> recordingIds);
        Artist GetArtistInfo(DiscogsMetadataProxySettings settings, string lidarrId, int metadataProfileId);
    }
}
