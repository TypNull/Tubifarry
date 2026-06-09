using Tubifarry.Core.Records;

namespace Tubifarry.Metadata.Lyrics.Converters
{
    public class PlainTextConverter : LyricConverterBase
    {
        public override Lyric? Read(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return null;

            List<LyricLine> lines = [.. content
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => new LyricLine { Text = l.Trim() })];

            return lines.Count > 0 ? new Lyric { Lines = lines } : null;
        }

        public override string? Write(Lyric lyric)
        {
            if (lyric.Lines.Count == 0)
                return null;

            return string.Join(Environment.NewLine, lyric.Lines.Select(l => l.Text));
        }
    }
}
