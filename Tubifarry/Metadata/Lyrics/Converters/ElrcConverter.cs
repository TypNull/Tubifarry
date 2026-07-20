using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Tubifarry.Core.Records;

namespace Tubifarry.Metadata.Lyrics.Converters;

public partial class ElrcConverter : LyricConverterBase
{
    public override Lyric? Read(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        string? artist = null, title = null, album = null;
        int duration = 0;
        List<LyricLine> lines = [];

        foreach (string raw in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();

            Match header = HeaderRegex().Match(line);
            if (header.Success)
            {
                string tag = header.Groups[1].Value.ToLowerInvariant();
                string value = header.Groups[2].Value;
                switch (tag)
                {
                    case "ar": artist = value; break;
                    case "al": album = value; break;
                    case "ti": title = value; break;
                    case "length":
                        long? lengthMs = ParseTimestamp(value);
                        if (lengthMs.HasValue) duration = (int)(lengthMs.Value / 1000);
                        break;
                }
                continue;
            }

            Match lineMatch = LineRegex().Match(line);
            if (!lineMatch.Success)
                continue;

            long? lineStart = ParseTimestamp(lineMatch.Groups[1].Value);
            string remainder = lineMatch.Groups[2].Value;

            MatchCollection wordStamps = WordRegex().Matches(remainder);
            if (wordStamps.Count == 0)
            {
                string plain = remainder.Trim();

                if (plain.Length > 0 || lineStart.HasValue)
                    lines.Add(new LyricLine { Text = plain, StartMs = lineStart });
                continue;
            }

            List<LyricLine> words = [];
            StringBuilder lineText = new();
            for (int i = 0; i < wordStamps.Count; i++)
            {
                Match stamp = wordStamps[i];
                long? wordStart = ParseTimestamp(stamp.Groups[1].Value);
                int textStart = stamp.Index + stamp.Length;
                int textEnd = i + 1 < wordStamps.Count ? wordStamps[i + 1].Index : remainder.Length;
                string wordText = remainder[textStart..textEnd];

                if (string.IsNullOrWhiteSpace(wordText))
                    continue;

                words.Add(new LyricLine { Text = wordText.Trim(), StartMs = wordStart });
                lineText.Append(wordText);
            }

            long? resolvedStart = lineStart ?? words.FirstOrDefault()?.StartMs ?? ParseTimestamp(wordStamps[0].Groups[1].Value);
            lines.Add(new LyricLine
            {
                Text = lineText.ToString().Trim(),
                StartMs = resolvedStart,
                Words = words.Count > 0 ? words : null
            });
        }

        if (lines.Count == 0)
            return null;

        return new Lyric { Lines = lines, Artist = artist, Title = title, Album = album, Duration = duration };
    }

    public override string? Write(Lyric lyric)
    {
        if (lyric.Lines.Count == 0)
            return null;

        StringBuilder sb = new();

        if (!string.IsNullOrEmpty(lyric.Artist))
            sb.AppendLine($"[ar:{lyric.Artist}]");
        if (!string.IsNullOrEmpty(lyric.Album))
            sb.AppendLine($"[al:{lyric.Album}]");
        if (!string.IsNullOrEmpty(lyric.Title))
            sb.AppendLine($"[ti:{lyric.Title}]");
        if (lyric.Duration > 0)
            sb.AppendLine($"[length:{FormatTag(lyric.Duration * 1000L)}]");
        sb.AppendLine("[by:Tubifarry Lyrics Enhancer]");
        sb.AppendLine();

        foreach (LyricLine line in lyric.Lines.Where(l => !string.IsNullOrEmpty(l.Text) || l.StartMs.HasValue).OrderBy(l => l.StartMs ?? 0))
        {
            if (!line.StartMs.HasValue)
            {
                sb.AppendLine(line.Text);
                continue;
            }

            sb.Append($"[{FormatTag(line.StartMs.Value)}]");

            if (line.Words?.Count > 0)
            {
                foreach (LyricLine word in line.Words)
                {
                    if (word.StartMs.HasValue)
                        sb.Append($" <{FormatTag(word.StartMs.Value)}>{word.Text}");
                    else
                        sb.Append($" {word.Text}");
                }
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine($" {line.Text}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static long? ParseTimestamp(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string[] parts = value.Trim().Split(':');
        try
        {
            double minutes, seconds;
            if (parts.Length == 2)
            {
                minutes = double.Parse(parts[0], CultureInfo.InvariantCulture);
                seconds = double.Parse(parts[1], CultureInfo.InvariantCulture);
            }
            else if (parts.Length == 3)
            {
                minutes = (double.Parse(parts[0], CultureInfo.InvariantCulture) * 60) + double.Parse(parts[1], CultureInfo.InvariantCulture);
                seconds = double.Parse(parts[2], CultureInfo.InvariantCulture);
            }
            else
            {
                return null;
            }
            return (long)Math.Round(((minutes * 60) + seconds) * 1000);
        }
        catch
        {
            return null;
        }
    }

    private static string FormatTag(long ms)
    {
        TimeSpan ts = TimeSpan.FromMilliseconds(ms);
        return $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 10:D2}";
    }

    [GeneratedRegex(@"^\[([a-zA-Z]+):(.+)\]$", RegexOptions.Compiled)]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"^\[(\d{1,2}:\d{2}(?:\.\d{1,3})?)\](.*)$", RegexOptions.Compiled)]
    private static partial Regex LineRegex();

    [GeneratedRegex(@"<(\d{1,2}:\d{2}(?:\.\d{1,3})?)>", RegexOptions.Compiled)]
    private static partial Regex WordRegex();
}
