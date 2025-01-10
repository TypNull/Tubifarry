using NzbDrone.Core.Music;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using System.Text.RegularExpressions;

namespace Tubifarry.Core
{
    public class ReleaseFormatter
    {
        private readonly ReleaseInfo _releaseInfo;
        private readonly Artist _artist;
        private readonly NamingConfig? _namingConfig;

        public ReleaseFormatter(ReleaseInfo releaseInfo, Artist artist, NamingConfig? namingConfig)
        {
            _releaseInfo = releaseInfo;
            _artist = artist;
            _namingConfig = namingConfig;
        }

        public string BuildTrackFilename(string? pattern, Track track, Album album)
        {
            pattern ??= _namingConfig?.StandardTrackFormat ?? "{track:0} {Track Title}";
            Dictionary<string, Func<string>> tokenHandlers = GetTokenHandlers(track, album);
            string formattedString = ReplaceTokens(pattern, tokenHandlers);
            return CleanFileName(formattedString);
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

        private Dictionary<string, Func<string>> GetTokenHandlers(Track? track, Album? album)
        {
            return new Dictionary<string, Func<string>>(StringComparer.OrdinalIgnoreCase)
        {
            // Album Tokens (only added if album is provided)
            { "{Album Title}", () => album?.Title ?? string.Empty },
            { "{Album CleanTitle}", () => CleanTitle(album?.Title) },
            { "{Album TitleThe}", () => TitleThe(album?.Title) },
            { "{Album CleanTitleThe}", () => CleanTitleThe(album?.Title) },
            { "{Album Type}", () => album?.AlbumType ?? string.Empty },
            { "{Album Genre}", () => album?.Genres?.FirstOrDefault() ?? string.Empty },
            { "{Album MbId}", () => album?.ForeignAlbumId ?? string.Empty },
            { "{Album Disambiguation}", () => album?.Disambiguation ?? string.Empty },
            { "{Release Year}", () => album?.ReleaseDate?.Year.ToString() ?? string.Empty },

            // Artist Tokens
            { "{Artist Name}", () => _artist?.Name ?? string.Empty },
            { "{Artist CleanName}", () => CleanTitle(_artist?.Name) },
            { "{Artist NameThe}", () => TitleThe(_artist?.Name) },
            { "{Artist CleanNameThe}", () => CleanTitleThe(_artist?.Name) },
            { "{Artist Genre}", () => _artist?.Metadata?.Value?.Genres?.FirstOrDefault() ?? string.Empty },
            { "{Artist MbId}", () => _artist?.ForeignArtistId ?? string.Empty },
            { "{Artist Disambiguation}", () => _artist?.Metadata?.Value?.Disambiguation ?? string.Empty },
            { "{Artist NameFirstCharacter}", () => TitleFirstCharacter(_artist?.Name) },

            // Track Tokens (only added if track is provided)
            { "{Track Title}", () => track?.Title ?? string.Empty },
            { "{Track CleanTitle}", () => CleanTitle(track?.Title) },
            { "{Track ArtistName}", () => _artist?.Name ?? string.Empty },
            { "{Track ArtistNameThe}", () => TitleThe(_artist?.Name) },
            { "{Track ArtistMbId}", () => _artist?.ForeignArtistId ?? string.Empty },
            { "{track:0}", () => FormatTrackNumber(track?.TrackNumber, "0") },
            { "{track:00}", () => FormatTrackNumber(track?.TrackNumber, "00") },

            // Medium Tokens (only added if track is provided)
            { "{Medium Name}", () => track?.AlbumRelease?.Value?.Media?.FirstOrDefault(m => m.Number == track.MediumNumber)?.Name ?? string.Empty },
            { "{medium:0}", () => track?.MediumNumber.ToString("0") ?? string.Empty },
            { "{medium:00}", () => track?.MediumNumber.ToString("00") ?? string.Empty },

            // Release Info Tokens
            { "{Original Title}", () => _releaseInfo?.Title ?? string.Empty }
        };
        }

        private static string ReplaceTokens(string pattern, Dictionary<string, Func<string>> tokenHandlers) => Regex.Replace(pattern, @"\{([^}]+)\}", match =>
        {
            string token = match.Groups[1].Value;
            if (tokenHandlers.TryGetValue($"{{{token}}}", out Func<string>? handler))
                return handler();

            return string.Empty;
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
                    fileName = Regex.Replace(fileName, @":\s*", " - ");
                    break;
            }

            return fileName.Trim();
        }

        private static string CleanTitle(string? title)
        {
            if (string.IsNullOrEmpty(title)) return string.Empty;
            return title.Replace("&", "and").Replace("/", " ").Replace("\\", " ").Trim();
        }

        private static string TitleThe(string? title)
        {
            if (string.IsNullOrEmpty(title)) return string.Empty;
            return Regex.Replace(title, @"^(The|A|An)\s+(.+)$", "$2, $1", RegexOptions.IgnoreCase);
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
    }
}