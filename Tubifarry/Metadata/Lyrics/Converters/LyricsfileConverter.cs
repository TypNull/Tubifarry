using Tubifarry.Core.Records;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Tubifarry.Metadata.Lyrics.Converters
{
    public class LyricsfileConverter : LyricConverterBase
    {
        private static readonly PlainTextConverter _plain = new();

        private static readonly IDeserializer _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        private static readonly ISerializer _serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();

        public override Lyric? Read(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return null;

            LyricsFileModel? model;
            try
            {
                model = _deserializer.Deserialize<LyricsFileModel>(content);
            }
            catch
            {
                return null;
            }

            if (model?.Lines == null)
                return null;

            List<LyricLine> lines = [];
            foreach (LineModel line in model.Lines)
            {
                List<LyricLine>? words = null;
                if (line.Words?.Count > 0)
                {
                    words = [];
                    foreach (WordModel word in line.Words)
                        words.Add(new LyricLine { Text = word.Text ?? string.Empty, StartMs = word.StartMs });
                }

                lines.Add(new LyricLine
                {
                    Text = line.Text ?? string.Empty,
                    StartMs = line.StartMs,
                    EndMs = line.EndMs,
                    Words = words
                });
            }

            if (lines.Count == 0)
                return null;

            return new Lyric
            {
                Lines = lines,
                Title = model.Metadata?.Title,
                Artist = model.Metadata?.Artist,
                Album = model.Metadata?.Album,
                Duration = model.Metadata?.DurationMs is long ms ? (int)(ms / 1000) : 0
            };
        }

        public override string? Write(Lyric lyric)
        {
            if (lyric.Lines.Count == 0)
                return null;

            LyricsFileModel model = new()
            {
                Version = "1.0",
                Metadata = new MetadataModel
                {
                    Title = lyric.Title,
                    Artist = lyric.Artist,
                    Instrumental = false,
                    Album = lyric.Album,
                    DurationMs = lyric.Duration > 0 ? lyric.Duration * 1000L : null
                },
                Lines = [],
                Plain = _plain.Write(lyric)
            };

            foreach (LyricLine line in lyric.Lines)
            {
                List<WordModel> words = [];
                if (line.Words != null)
                    foreach (LyricLine word in line.Words)
                        words.Add(new WordModel { Text = word.Text, StartMs = word.StartMs });

                model.Lines.Add(new LineModel
                {
                    Text = line.Text,
                    Words = words,
                    StartMs = line.StartMs,
                    EndMs = line.EndMs
                });
            }

            return _serializer.Serialize(model);
        }

        private sealed class LyricsFileModel
        {
            public string? Version { get; set; }
            public MetadataModel? Metadata { get; set; }
            public List<LineModel>? Lines { get; set; }
            public string? Plain { get; set; }
        }

        private sealed class MetadataModel
        {
            public string? Title { get; set; }
            public string? Artist { get; set; }
            public bool Instrumental { get; set; }
            public string? Album { get; set; }
            public long? DurationMs { get; set; }
        }

        private sealed class LineModel
        {
            public string? Text { get; set; }
            public List<WordModel>? Words { get; set; }
            public long? StartMs { get; set; }
            public long? EndMs { get; set; }
        }

        private sealed class WordModel
        {
            public string? Text { get; set; }
            public long? StartMs { get; set; }
        }
    }
}
