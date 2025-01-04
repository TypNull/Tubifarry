using System.Xml.Linq;

namespace Tubifarry.ImportLists
{
    public record MusicBrainzSearchItem(string? Title, string? AlbumId, string? Artist, string? ArtistId, DateTime ReleaseDate)
    {
        public static MusicBrainzSearchItem FromXml(XElement release, XNamespace ns)
        {
            XElement? artistCredit = release.Element(ns + "artist-credit")?.Element(ns + "name-credit")?.Element(ns + "artist");
            XElement? releaseGroup = release.Element(ns + "release-group");

            return new MusicBrainzSearchItem(release.Element(ns + "title")?.Value, releaseGroup?.Attribute("id")?.Value, artistCredit?.Element(ns + "name")?.Value,
                artistCredit?.Attribute("id")?.Value, DateTime.TryParse(release.Element(ns + "date")?.Value, out DateTime date) ? date : DateTime.MinValue);
        }
    }

    public record MusicBrainzAlbumItem(string? AlbumId, string? Title, string? Type, string? PrimaryType, string? Artist, string? ArtistId, DateTime ReleaseDate)
    {
        public static MusicBrainzAlbumItem? FromXml(XElement releaseGroup, XNamespace ns)
        {
            if (releaseGroup == null)
                return null;

            return new MusicBrainzAlbumItem(releaseGroup.Attribute("id")?.Value, releaseGroup.Element(ns + "title")?.Value, releaseGroup.Attribute("type")?.Value,
                releaseGroup.Element(ns + "primary-type")?.Value, releaseGroup.Element(ns + "artist-credit")?.Element(ns + "name-credit")?.Element(ns + "artist")?.Element(ns + "name")?.Value, releaseGroup.Element(ns + "artist-credit")?.Element(ns + "name-credit")?.Element(ns + "artist")?.Attribute("id")?.Value, DateTime.TryParse(releaseGroup.Element(ns + "first-release-date")?.Value, out DateTime date) ? date : DateTime.MinValue);
        }
    }
}
