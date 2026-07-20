using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Tubifarry.Core.Records;

namespace Tubifarry.Metadata.Lyrics.Converters
{
    public class TtmlConverter : LyricConverterBase
    {
        private static readonly XNamespace Tt = "http://www.w3.org/ns/ttml";
        private static readonly XNamespace Ttm = "http://www.w3.org/ns/ttml#metadata";
        private static readonly XNamespace Itunes = "http://music.apple.com/lyric-ttml-internal";

        public override Lyric? Read(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return null;

            XDocument doc;
            try
            {
                doc = XDocument.Parse(content, LoadOptions.PreserveWhitespace);
            }
            catch
            {
                return null;
            }

            XElement? body = doc.Root?.Element(Tt + "body");
            if (body == null)
                return null;

            List<LyricLine> lines = [];
            foreach (XElement p in body.Descendants(Tt + "p"))
            {
                LyricLine? line = ParseParagraph(p);
                if (line != null)
                    lines.Add(line);
            }

            if (lines.Count == 0)
                return null;

            XElement? meta = doc.Root?.Element(Tt + "head")?.Element(Tt + "metadata");
            string? artist = (string?)meta?.Descendants(Itunes + "songwriter").FirstOrDefault();
            string? title = (string?)meta?.Elements().FirstOrDefault(e => e.Name.LocalName == "title");

            int duration = 0;
            long? dur = ParseTimestamp((string?)body.Attribute("dur"));
            if (dur.HasValue)
                duration = (int)(dur.Value / 1000);

            return new Lyric { Lines = lines, Artist = artist, Title = title, Duration = duration };
        }

        private static LyricLine? ParseParagraph(XElement p)
        {
            long? lineStart = ParseTimestamp((string?)p.Attribute("begin"));
            long? lineEnd = ParseTimestamp((string?)p.Attribute("end"));

            bool hasTimedSpans = p.Elements(Tt + "span")
                .Any(s => (string?)s.Attribute(Ttm + "role") != "x-bg" && s.Attribute("begin") != null);

            if (!hasTimedSpans)
            {
                string plainOnly = p.Value.Trim();
                return string.IsNullOrEmpty(plainOnly) && !lineStart.HasValue
                    ? null
                    : new LyricLine { Text = plainOnly, StartMs = lineStart, EndMs = lineEnd };
            }

            List<LyricLine> words = [];
            StringBuilder current = new();
            long? wordStart = null;
            long? wordEnd = null;

            void Flush()
            {
                string text = current.ToString().Trim();
                if (text.Length > 0)
                    words.Add(new LyricLine { Text = text, StartMs = wordStart, EndMs = wordEnd });
                current.Clear();
                wordStart = null;
                wordEnd = null;
            }

            foreach (XNode node in p.Nodes())
            {
                if (node is XText textNode)
                {
                    if (string.IsNullOrWhiteSpace(textNode.Value))
                        Flush();
                    else
                        current.Append(textNode.Value);
                    continue;
                }

                if (node is not XElement span || span.Name != Tt + "span")
                    continue;

                if ((string?)span.Attribute(Ttm + "role") == "x-bg")
                {
                    Flush();
                    continue;
                }

                current.Append(span.Value);
                wordStart ??= ParseTimestamp((string?)span.Attribute("begin"));
                long? spanEnd = ParseTimestamp((string?)span.Attribute("end"));
                if (spanEnd.HasValue)
                    wordEnd = spanEnd;
            }
            Flush();

            if (words.Count > 0)
            {
                StringBuilder lineText = new();
                foreach (LyricLine w in words)
                {
                    if (lineText.Length > 0)
                        lineText.Append(' ');
                    lineText.Append(w.Text);
                }

                return new LyricLine
                {
                    Text = lineText.ToString(),
                    StartMs = lineStart ?? words[0].StartMs,
                    EndMs = lineEnd ?? words[^1].EndMs,
                    Words = words
                };
            }

            string plain = p.Value.Trim();
            if (string.IsNullOrEmpty(plain) && !lineStart.HasValue)
                return null;

            return new LyricLine { Text = plain, StartMs = lineStart, EndMs = lineEnd };
        }

        public override string? Write(Lyric lyric)
        {
            if (lyric.Lines.Count == 0)
                return null;

            bool word = lyric.HasWordSync;

            XElement head = new(Tt + "head",
                new XElement(Tt + "metadata",
                    string.IsNullOrEmpty(lyric.Title) ? null : new XElement(Tt + "title", lyric.Title)));

            XElement div = new(Tt + "div");
            foreach (LyricLine line in lyric.Lines.Where(l => !string.IsNullOrEmpty(l.Text) || l.StartMs.HasValue).OrderBy(l => l.StartMs ?? 0))
            {
                XElement p = new(Tt + "p", new XText(line.Text));
                if (line.StartMs.HasValue)
                    p.SetAttributeValue("begin", FormatTimestamp(line.StartMs.Value));
                if (line.EndMs.HasValue)
                    p.SetAttributeValue("end", FormatTimestamp(line.EndMs.Value));

                if (word && line.Words?.Count > 0)
                {
                    p.RemoveNodes();
                    foreach (LyricLine w in line.Words)
                    {
                        XElement span = new(Tt + "span", new XText(w.Text));
                        if (w.StartMs.HasValue)
                            span.SetAttributeValue("begin", FormatTimestamp(w.StartMs.Value));
                        if (w.EndMs.HasValue)
                            span.SetAttributeValue("end", FormatTimestamp(w.EndMs.Value));
                        p.Add(span);
                    }
                }
                div.Add(p);
            }

            XElement body = new(Tt + "body", div);
            if (lyric.Duration > 0)
                body.SetAttributeValue("dur", FormatTimestamp(lyric.Duration * 1000L));

            XElement tt = new(Tt + "tt", new XAttribute(XNamespace.Xml + "lang", "en"), head, body);
            tt.SetAttributeValue(Itunes + "timing", word ? "Word" : "Line");

            return new XDocument(new XDeclaration("1.0", "UTF-8", null), tt).ToString();
        }

        private static long? ParseTimestamp(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            value = value.Trim();
            if (value.EndsWith('s') && !value.Contains(':'))
                value = value[..^1];

            string[] parts = value.Split(':');
            try
            {
                double hours = 0, minutes = 0, seconds;
                switch (parts.Length)
                {
                    case 1:
                        seconds = double.Parse(parts[0], CultureInfo.InvariantCulture);
                        break;
                    case 2:
                        minutes = double.Parse(parts[0], CultureInfo.InvariantCulture);
                        seconds = double.Parse(parts[1], CultureInfo.InvariantCulture);
                        break;
                    case 3:
                        hours = double.Parse(parts[0], CultureInfo.InvariantCulture);
                        minutes = double.Parse(parts[1], CultureInfo.InvariantCulture);
                        seconds = double.Parse(parts[2], CultureInfo.InvariantCulture);
                        break;
                    default:
                        return null;
                }
                return (long)Math.Round(((hours * 3600) + (minutes * 60) + seconds) * 1000);
            }
            catch
            {
                return null;
            }
        }

        private static string FormatTimestamp(long ms)
        {
            TimeSpan ts = TimeSpan.FromMilliseconds(ms);
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}"
                : $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
        }
    }
}
