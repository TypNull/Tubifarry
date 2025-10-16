using NzbDrone.Core.ImportLists.LastFm;

namespace Tubifarry.ImportLists.LastFmRecommendation
{
    public class LastFmTopResponse
    {
        public LastFmArtistList? TopArtists { get; set; }
        public LastFmAlbumList? TopAlbums { get; set; }
        public LastFmTrackList? TopTracks { get; set; }
    }

    public class LastFmTrackList
    {
        public List<LastFmTrack> Track { get; set; } = [];
    }

    public class LastFmTrack
    {
        public string Name { get; set; } = string.Empty;
        public int Duration { get; set; }
        public string Url { get; set; } = string.Empty;
        public LastFmArtist Artist { get; set; } = new();
    }

    public class LastFmSimilarArtistsResponse
    {
        public LastFmArtistList? SimilarArtists { get; set; }
    }

    public class LastFmSimilarTracksResponse
    {
        public LastFmTrackList? SimilarTracks { get; set; }
    }

    public class LastFmTopAlbumsResponse
    {
        public LastFmAlbumList? TopAlbums { get; set; }
    }
}
