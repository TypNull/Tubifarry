using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Tubifarry.Core.Records;

namespace Tubifarry.Metadata.Lyrics.Converters;

public partial class LrcConverter : LyricConverterBase
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

            MatchCollection stamps = TimestampRegex().Matches(line);
            if (stamps.Count > 0)
            {
                string text = line[(stamps[^1].Index + stamps[^1].Length)..].Trim();
                foreach (Match stamp in stamps)
                {
                    long? ms = ParseTimestamp(stamp.Groups[1].Value);
                    if (ms.HasValue)
                        lines.Add(new LyricLine { Text = text, StartMs = ms });
                }
            }
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
            sb.AppendLine($"[length:{FormatTimestamp(lyric.Duration * 1000L)}]");
        sb.AppendLine("[by:Tubifarry Lyrics Enhancer]");
        sb.AppendLine();

        foreach (LyricLine line in lyric.Lines.Where(l => !string.IsNullOrEmpty(l.Text) || l.StartMs.HasValue).OrderBy(l => l.StartMs ?? 0))
        {
            if (line.StartMs.HasValue)
                sb.AppendLine($"[{FormatTimestamp(line.StartMs.Value)}] {line.Text}");
            else
                sb.AppendLine(line.Text);
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

    private static string FormatTimestamp(long ms)
    {
        TimeSpan ts = TimeSpan.FromMilliseconds(ms);
        return $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 10:D2}";
    }

    [GeneratedRegex(@"^\[([a-zA-Z]+):(.+)\]$", RegexOptions.Compiled)]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"\[(\d{1,2}:\d{2}(?:\.\d{1,3})?)\]", RegexOptions.Compiled)]
    private static partial Regex TimestampRegex();
}
