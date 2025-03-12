using DryIoc.ImTools;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Profiles.Metadata;
using System.Globalization;

namespace Tubifarry.Metadata.Proxy.Core
{
    public static class AlbumMapper
    {
        /// <summary>
        /// Primary type mapping: maps strings to a standard primary album type.
        /// </summary>
        public static readonly Dictionary<string, string> PrimaryTypeMap = new(StringComparer.OrdinalIgnoreCase)
        {
            { "album", "Album" },
            { "broadcast", "Broadcast" },
            { "ep", "EP" },
            { "single", "Single" }
        };

        /// <summary>
        /// Secondary type keywords used for determining types based on title.
        /// </summary>
        public static readonly Dictionary<SecondaryAlbumType, List<string>> SecondaryTypeKeywords = new()
        {
            { SecondaryAlbumType.Live, new List<string> { "live", "concert", "performance", "stage", " at " } },
            { SecondaryAlbumType.Remix, new List<string> { "remix", "remaster", "rework", "reimagined" } },
            { SecondaryAlbumType.Compilation, new List<string> { "greatest hits", "best of", "collection", "anthology" } },
            { SecondaryAlbumType.Soundtrack, new List<string> { "soundtrack", "ost", "original score" } },
            { SecondaryAlbumType.Spokenword, new List<string> { "spoken", "poetry", "lecture", "speech" } },
            { SecondaryAlbumType.Interview, new List<string> { "interview", "q&a", "conversation" } },
            { SecondaryAlbumType.Audiobook, new List<string> { "audiobook", " unabridged", " narration" } },
            { SecondaryAlbumType.Demo, new List<string> { "demo", "unreleased", "rough mix" } },
            { SecondaryAlbumType.Mixtape, new List<string> { "mixtape", "street", "underground" } },
            { SecondaryAlbumType.DJMix, new List<string> { "dj mix", " dj ", "set" } },
            { SecondaryAlbumType.Audiodrama, new List<string> { "audio drama", "radio play", "theater" } }
        };

        /// <summary>
        /// Direct mapping of strings to SecondaryAlbumType (used by Discogs).
        /// </summary>
        public static readonly Dictionary<string, SecondaryAlbumType> SecondaryTypeMap = new(StringComparer.OrdinalIgnoreCase)
        {
            { "compilation", SecondaryAlbumType.Compilation },
            { "studio", SecondaryAlbumType.Studio },
            { "soundtrack", SecondaryAlbumType.Soundtrack },
            { "spokenword", SecondaryAlbumType.Spokenword },
            { "interview", SecondaryAlbumType.Interview },
            { "live", SecondaryAlbumType.Live },
            { "remix", SecondaryAlbumType.Remix },
            { "dj mix", SecondaryAlbumType.DJMix },
            { "mixtape", SecondaryAlbumType.Mixtape },
            { "demo", SecondaryAlbumType.Demo },
            { "audio drama", SecondaryAlbumType.Audiodrama },
            { "master", new() { Id = 36, Name = "Master" } },
            { "release", new() { Id = 37, Name = "Release" } },
        };

        /// <summary>
        /// Extracts a link name from a URL.
        /// </summary>
        /// <param name="url">The URL to extract the link name from.</param>
        /// <returns>A human-readable name for the link (e.g., "Bandcamp", "YouTube").</returns>
        public static string GetLinkNameFromUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return "Website";

            try
            {
                Uri uri = new(url);
                string[] hostParts = uri.Host.Split('.', StringSplitOptions.RemoveEmptyEntries);

                if (hostParts.Contains("bandcamp"))
                    return "Bandcamp";
                if (hostParts.Contains("facebook"))
                    return "Facebook";
                if (hostParts.Contains("youtube"))
                    return "YouTube";
                if (hostParts.Contains("soundcloud"))
                    return "SoundCloud";
                if (hostParts.Contains("discogs"))
                    return "Discogs";

                string mainDomain = hostParts.Length > 1 ? hostParts[^2] : hostParts[0];
                return mainDomain.ToUpper(CultureInfo.InvariantCulture);
            }
            catch { return "Website"; }
        }

        /// <summary>
        /// Determines secondary album types from a title using keyword matching.
        /// </summary>
        /// <param name="title">The title of the album to analyze.</param>
        /// <returns>A list of detected secondary album types.</returns>
        public static List<SecondaryAlbumType> DetermineSecondaryTypesFromTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return new List<SecondaryAlbumType>();

            string cleanTitle = Parser.NormalizeTitle(title).ToLowerInvariant();
            List<SecondaryAlbumType> detectedTypes = new();

            foreach (KeyValuePair<SecondaryAlbumType, List<string>> kvp in SecondaryTypeKeywords)
            {
                if (kvp.Value.Any(keyword => cleanTitle.Contains(keyword)))
                    detectedTypes.Add(kvp.Key);
            }

            // In this example, if both Live and Remix are detected, remove Remix.
            if (detectedTypes.Contains(SecondaryAlbumType.Live) && detectedTypes.Contains(SecondaryAlbumType.Remix))
                detectedTypes.Remove(SecondaryAlbumType.Remix);

            return detectedTypes.Distinct().ToList();
        }

        /// <summary>
        /// Filters albums based on a metadata profile.
        /// </summary>
        /// <param name="albums">The collection of albums to filter.</param>
        /// <param name="metadataProfileId">The ID of the metadata profile to use for filtering.</param>
        /// <param name="metadataProfileService">The service to retrieve metadata profiles.</param>
        /// <returns>A filtered collection of albums that match the metadata profile criteria.</returns>
        public static List<Album> FilterAlbums(IEnumerable<Album> albums, int metadataProfileId, IMetadataProfileService metadataProfileService)
        {
            MetadataProfile metadataProfile = metadataProfileService.Exists(metadataProfileId) ? metadataProfileService.Get(metadataProfileId) : metadataProfileService.All().First();
            List<string> primaryTypes = new(metadataProfile.PrimaryAlbumTypes.Where(s => s.Allowed).Select(s => s.PrimaryAlbumType.Name));
            List<string> secondaryTypes = new(metadataProfile.SecondaryAlbumTypes.Where(s => s.Allowed).Select(s => s.SecondaryAlbumType.Name));
            List<string> releaseStatuses = new(metadataProfile.ReleaseStatuses.Where(s => s.Allowed).Select(s => s.ReleaseStatus.Name));

            return albums.Where(album => primaryTypes.Contains(album.AlbumType) &&
                                (!album.SecondaryTypes.Any() && secondaryTypes.Contains("Studio") ||
                                 album.SecondaryTypes.Any(x => secondaryTypes.Contains(x.Name))) &&
                                album.AlbumReleases.Value.Any(x => releaseStatuses.Contains(x.Status))).ToList();
        }

        /// <summary>
        /// Maps album types based on a collection of format description strings.
        /// Sets both the primary album type and the secondary types.
        /// </summary>
        /// <param name="formatDescriptions">The collection of format descriptions to analyze.</param>
        /// <param name="album">The album object to update with the mapped types.</param>
        public static void MapAlbumTypes(IEnumerable<string>? formatDescriptions, Album album)
        {
            album.AlbumType = "Album";
            if (formatDescriptions != null)
            {
                foreach (string desc in formatDescriptions)
                {
                    if (PrimaryTypeMap.TryGetValue(desc.ToLowerInvariant(), out string? primaryType))
                    {
                        album.AlbumType = primaryType;
                        break;
                    }
                }

                HashSet<SecondaryAlbumType> secondaryTypes = new();
                foreach (string desc in formatDescriptions)
                {
                    if (SecondaryTypeMap.TryGetValue(desc.ToLowerInvariant(), out SecondaryAlbumType? secondaryType))
                        secondaryTypes.Add(secondaryType);
                }
                album.SecondaryTypes = secondaryTypes.ToList();
            }
        }
    }
}