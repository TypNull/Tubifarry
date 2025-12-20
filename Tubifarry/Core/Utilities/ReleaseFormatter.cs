using NzbDrone.Core.Music;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using System.Text.RegularExpressions;

namespace Tubifarry.Core.Utilities
{
    public partial class ReleaseFormatter(ReleaseInfo releaseInfo, Artist artist, NamingConfig? namingConfig)
    {
        private readonly ReleaseInfo _releaseInfo = releaseInfo;
        private readonly Artist _artist = artist;
        private readonly NamingConfig? _namingConfig = namingConfig;

        public string BuildTrackFilename(string? pattern, Track track, Album album)
        {
            pattern ??= _namingConfig?.StandardTrackFormat ?? "{track:0} {Track Title}";
            Dictionary<string, Func<string>> tokenHandlers = GetTokenHandlers(track, album);
            string formattedString = ReplaceTokens(pattern, tokenHandlers);
            return CleanFileName(Path.GetFileName(formattedString));
        }

        public string BuildAlbumFilename(string? pattern, Album album)
        {
            pattern ??= "{Album Title}";
            Dictionary<string, Func<string>> tokenHandlers = GetTokenHandlers(null, album);
            string formattedString = ReplaceTokens(pattern, tokenHandlers);
            return CleanFileName(formattedString);
        }

        public string BuildArtistFolderName(string? pattern)
        {
            pattern ??= _namingConfig?.ArtistFolderFormat ?? "{Artist Name}";
            Dictionary<string, Func<string>> tokenHandlers = GetTokenHandlers(null, null);
            string formattedString = ReplaceTokens(pattern, tokenHandlers);
            return CleanFileName(formattedString);
        }

        private Dictionary<string, Func<string>> GetTokenHandlers(Track? track, Album? album) => new(StringComparer.OrdinalIgnoreCase)
        {
            // Album Tokens (only added if album is provided)
            { "{Album Title}", () => CleanTitle(album?.Title) },
            { "{Album CleanTitle}", () => CleanTitle(album?.Title) },
            { "{Album TitleThe}", () => CleanTitle(TitleThe(album?.Title)) },
            { "{Album CleanTitleThe}", () => CleanTitle(album?.Title) },
            { "{Album Type}", () => CleanTitle(album?.AlbumType) },
            { "{Album Genre}", () => CleanTitle(album?.Genres?.FirstOrDefault()) },
            { "{Album MbId}", () => album?.ForeignAlbumId ?? string.Empty },
            { "{Album Disambiguation}", () => CleanTitle(album?.Disambiguation) },
            { "{Release Year}", () => album?.ReleaseDate?.Year.ToString() ?? string.Empty },

            // Artist Tokens
            { "{Artist Name}", () => CleanTitle(_artist?.Name) },
            { "{Artist CleanName}", () => CleanTitle(_artist?.Name) },
            { "{Artist NameThe}", () => CleanTitle(TitleThe(_artist?.Name))},
            { "{Artist CleanNameThe}", () => CleanTitleThe(_artist?.Name) },
            { "{Artist Genre}", () => CleanTitle(_artist?.Metadata?.Value?.Genres?.FirstOrDefault()) },
            { "{Artist MbId}", () => _artist?.ForeignArtistId ?? string.Empty },
            { "{Artist Disambiguation}", () => CleanTitle(_artist?.Metadata?.Value?.Disambiguation) },
            { "{Artist NameFirstCharacter}", () => TitleFirstCharacter(_artist?.Name) },

            // Track Tokens (only added if track is provided)
            { "{Track Title}", () => CleanTitle(track?.Title) },
            { "{Track CleanTitle}", () => CleanTitle(track?.Title) },
            { "{Track ArtistName}", () => CleanTitle(_artist?.Name) },
            { "{Track ArtistNameThe}", () => CleanTitle(TitleThe(_artist?.Name)) },
            { "{Track ArtistMbId}", () => _artist?.ForeignArtistId ?? string.Empty },
            { "{track:0}", () => FormatTrackNumber(track?.TrackNumber, "0") },
            { "{track:00}", () => FormatTrackNumber(track?.TrackNumber, "00") },

            // Medium Tokens (only added if track is provided)
            { "{Medium Name}", () => CleanTitle(track?.AlbumRelease?.Value?.Media?.FirstOrDefault(m => m.Number == track.MediumNumber)?.Name) },
            { "{medium:0}", () => track?.MediumNumber.ToString("0") ?? string.Empty },
            { "{medium:00}", () => track?.MediumNumber.ToString("00") ?? string.Empty },

            // Release Info Tokens
            { "{Original Title}", () => CleanTitle(_releaseInfo?.Title) }
        };

        private static string ReplaceTokens(string pattern, Dictionary<string, Func<string>> tokenHandlers) => ReplaceTokensRegex().Replace(pattern, match =>
        {
            string token = match.Groups[1].Value;
            return tokenHandlers.TryGetValue($"{{{token}}}", out Func<string>? handler) ? handler() : string.Empty;
        });

        private string CleanFileName(string fileName)
        {
            char[] invalidFileNameChars = Path.GetInvalidFileNameChars();
            char[] invalidPathChars = Path.GetInvalidPathChars();
            char[] invalidChars = invalidFileNameChars.Union(invalidPathChars).ToArray();
            fileName = invalidChars.Aggregate(fileName, (current, invalidChar) => current.Replace(invalidChar.ToString(), string.Empty));

            switch (_namingConfig?.ColonReplacementFormat)
            {
                case ColonReplacementFormat.Delete:
                    fileName = fileName.Replace(":", string.Empty);
                    break;

                case ColonReplacementFormat.Dash:
                    fileName = fileName.Replace(":", "-");
                    break;

                case ColonReplacementFormat.SpaceDash:
                    fileName = fileName.Replace(":", " -");
                    break;

                case ColonReplacementFormat.SpaceDashSpace:
                    fileName = fileName.Replace(":", " - ");
                    break;

                case ColonReplacementFormat.Smart:
                    fileName = ColonReplaceRegex().Replace(fileName, " - ");
                    break;
            }

            return fileName.Trim();
        }

        private static string CleanTitle(string? title)
        {
            if (string.IsNullOrEmpty(title)) return string.Empty;
            return title.Replace("&", "and").Replace("/", " - ").Replace("\\", " - ").Trim();
        }

        private static string TitleThe(string? title)
        {
            if (string.IsNullOrEmpty(title)) return string.Empty;
            return TitleTheRegex().Replace(title, "$2, $1");
        }

        private static string CleanTitleThe(string? title) => CleanTitle(TitleThe(title));

        private static string TitleFirstCharacter(string? title)
        {
            if (string.IsNullOrEmpty(title)) return "_";
            return char.IsLetterOrDigit(title[0]) ? title[..1].ToUpper() : "_";
        }

        private static string FormatTrackNumber(string? trackNumber, string? format)
        {
            if (string.IsNullOrEmpty(trackNumber)) return string.Empty;
            if (int.TryParse(trackNumber, out int trackNumberInt))
                return trackNumberInt.ToString(format);
            return trackNumber;
        }

        [GeneratedRegex(@"\{([^}]+)\}")]
        private static partial Regex ReplaceTokensRegex();

        [GeneratedRegex(@"^(The|A|An)\s+(.+)$", RegexOptions.IgnoreCase, "de-DE")]
        private static partial Regex TitleTheRegex();

        [GeneratedRegex(@":\s*")]
        private static partial Regex ColonReplaceRegex();
    }
}