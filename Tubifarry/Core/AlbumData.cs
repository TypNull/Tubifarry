using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using System.Text.RegularExpressions;

namespace Tubifarry.Core
{
    /// <summary>
    /// Contains combined information about an album, search parameters, and search results.
    /// </summary>
    public class AlbumData
    {
        public string IndexerName { get; }
        // Mixed
        public string AlbumId { get; set; } = string.Empty;

        // Properties from AlbumInfo
        public string AlbumName { get; set; } = string.Empty;
        public string ArtistName { get; set; } = string.Empty;
        public string InfoUrl { get; set; } = string.Empty;
        public string ReleaseDate { get; set; } = string.Empty;
        public DateTime ReleaseDateTime { get; set; }
        public string ReleaseDatePrecision { get; set; } = string.Empty;
        public int TotalTracks { get; set; }
        public bool ExplicitContent { get; set; }
        public string CustomString { get; set; } = string.Empty;
        public string CoverResolution { get; set; } = string.Empty;

        // Properties from YoutubeSearchResults
        public int Bitrate { get; set; }
        public long Duration { get; set; }

        // Soulseek
        public long? Size { get; set; }
        public int Priotity { get; set; }

        // Not used
        public AudioFormat Codec { get; set; } = AudioFormat.AAC;

        public AlbumData(string name) => IndexerName = name;

        /// <summary>
        /// Converts AlbumData into a ReleaseInfo object.
        /// </summary>
        public ReleaseInfo ToReleaseInfo() => new()
        {
            Guid = $"{IndexerName}-{AlbumId}-{Bitrate}",
            Artist = ArtistName,
            Album = AlbumName,
            DownloadUrl = AlbumId,
            InfoUrl = InfoUrl,
            PublishDate = ReleaseDateTime == DateTime.MinValue ? DateTime.UtcNow : ReleaseDateTime,
            DownloadProtocol = nameof(YoutubeDownloadProtocol),
            Title = ConstructTitle(),
            Codec = Codec.ToString(),
            Resolution = CoverResolution,
            Source = CustomString,
            Container = Bitrate.ToString(),
            Size = Size ?? (Duration > 0 ? Duration : TotalTracks * 300) * Bitrate * 1000 / 8
        };

        /// <summary>
        /// Parses the release date based on the precision.
        /// </summary>
        public void ParseReleaseDate() => ReleaseDateTime = ReleaseDatePrecision switch
        {
            "year" => new DateTime(int.Parse(ReleaseDate), 1, 1),
            "month" => DateTime.ParseExact(ReleaseDate, "yyyy-MM", System.Globalization.CultureInfo.InvariantCulture),
            "day" => DateTime.ParseExact(ReleaseDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new FormatException($"Unsupported release_date_precision: {ReleaseDatePrecision}"),
        };

        /// <summary>
        /// Constructs a title string for the album in a format optimized for parsing.
        /// </summary>
        /// <returns>A formatted title string.</returns>
        private string ConstructTitle()
        {
            string normalizedAlbumName = NormalizeAlbumName(AlbumName);

            string title = $"{ArtistName} - {normalizedAlbumName}";

            if (ReleaseDateTime != DateTime.MinValue)
                title += $" - {ReleaseDateTime.Year}";

            if (ExplicitContent)
                title += " [Explicit]";

            int calculatedBitrate = Bitrate;
            if (calculatedBitrate <= 0 && Size.HasValue && Duration > 0)
                calculatedBitrate = (int)((Size.Value * 8) / (Duration * 1000));

            if (AudioFormatHelper.IsLossyFormat(Codec))
                title += $" [{Codec} {calculatedBitrate}kbps] [WEB]";
            else
                title += $" [{Codec}] [WEB]";

            return title;
        }

        /// <summary>
        /// Normalizes the album name to handle featuring artists and other parentheses.
        /// </summary>
        /// <param name="albumName">The album name to normalize.</param>
        /// <returns>The normalized album name.</returns>
        private static string NormalizeAlbumName(string albumName)
        {
            Regex featRegex = new(@"(?i)\b(feat\.|ft\.|featuring)\b", RegexOptions.IgnoreCase);
            if (featRegex.IsMatch(albumName))
            {
                Match match = featRegex.Match(albumName);
                string featuringArtist = albumName.Substring(match.Index + match.Length).Trim();

                albumName = $"{albumName.Substring(0, match.Index).Trim()} (feat. {featuringArtist})";
            }
            albumName = Regex.Replace(albumName, @"\((?!feat\.)[^)]*\)", match => $"{{{match.Value.Trim('(', ')')}}}");

            return albumName;
        }
    }
}