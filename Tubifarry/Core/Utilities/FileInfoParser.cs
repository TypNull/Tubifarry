using System.Text.RegularExpressions;

namespace Tubifarry.Core.Utilities
{
    public class FileInfoParser
    {
        public string? Artist { get; private set; }
        public string? Title { get; private set; }
        public int TrackNumber { get; private set; }
        public int DiscNumber { get; private set; }
        public string? Tag { get; private set; }

        private static readonly List<Tuple<string, string>> CharsAndSeps =
        [
            Tuple.Create(@"a-z0-9,\(\)\.&'’\s", @"\s_-"),
            Tuple.Create(@"a-z0-9,\(\)\.\&'’_", @"\s-")
        ];

        public FileInfoParser(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));
            string filename = Path.GetFileNameWithoutExtension(filePath);
            ParseFilename(filename);
        }

        private void ParseFilename(string filename)
        {
            foreach (Tuple<string, string> charSep in CharsAndSeps)
            {
                foreach (Regex pattern in GeneratePatterns(charSep.Item1, charSep.Item2))
                {
                    Match match = pattern.Match(filename);
                    if (match.Success)
                    {
                        Artist = match.Groups["artist"].Success ? match.Groups["artist"].Value.Trim() : string.Empty;
                        Title = match.Groups["title"].Success ? match.Groups["title"].Value.Trim() : string.Empty;
                        TrackNumber = match.Groups["track"].Success ? int.Parse(match.Groups["track"].Value) : 0;
                        Tag = match.Groups["tag"].Success ? match.Groups["tag"].Value.Trim() : string.Empty;
                        if (TrackNumber > 100)
                        {
                            DiscNumber = TrackNumber / 100;
                            TrackNumber %= 100;
                        }
                        return;
                    }
                }
            }
        }

        private static Regex[] GeneratePatterns(string chars, string sep)
        {
            string sep1 = $"(?<sep>[{sep}]+)";
            const string sepn = @"\k<sep>";
            string artist = $"(?<artist>[{chars}]+)";
            const string track = @"(?<track>\d+)";
            string title = $"(?<title>[{chars}]+)";
            string tag = $"(?<tag>[{chars}]+)";

            return
            [
                new Regex($"^{track}{sep1}{artist}{sepn}{title}{sepn}{tag}$", RegexOptions.IgnoreCase),
                new Regex($"^{track}{sep1}{artist}{sepn}{tag}{sepn}{title}$", RegexOptions.IgnoreCase),
                new Regex($"^{track}{sep1}{artist}{sepn}{title}$", RegexOptions.IgnoreCase),

                new Regex($"^{artist}{sep1}{tag}{sepn}{track}{sepn}{title}$", RegexOptions.IgnoreCase),
                new Regex($"^{artist}{sep1}{track}{sepn}{title}{sepn}{tag}$", RegexOptions.IgnoreCase),
                new Regex($"^{artist}{sep1}{track}{sepn}{title}$", RegexOptions.IgnoreCase),

                new Regex($"^{artist}{sep1}{title}{sepn}{tag}$", RegexOptions.IgnoreCase),
                new Regex($"^{artist}{sep1}{tag}{sepn}{title}$", RegexOptions.IgnoreCase),
                new Regex($"^{artist}{sep1}{title}$", RegexOptions.IgnoreCase),

                new Regex($"^{track}{sep1}{title}$", RegexOptions.IgnoreCase),
                new Regex($"^{track}{sep1}{tag}{sepn}{title}$", RegexOptions.IgnoreCase),
                new Regex($"^{track}{sep1}{title}{sepn}{tag}$", RegexOptions.IgnoreCase),

                new Regex($"^{title}$", RegexOptions.IgnoreCase),
            ];
        }
    }
}

