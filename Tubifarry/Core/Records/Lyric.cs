namespace Tubifarry.Core.Records
{
    public record Lyric
    {
        public List<LyricLine> Lines { get; init; } = [];

        public string? Artist { get; init; }
        public string? Title { get; init; }
        public string? Album { get; init; }
        public int Duration { get; init; }

        public bool HasWordSync => Lines.Any(l => l.Words?.Count > 0);
        public bool HasLineSync => Lines.Any(l => l.StartMs.HasValue);
        public bool IsPlainText => Lines.Count > 0 && !HasLineSync;
    }

    public record LyricLine
    {
        public required string Text { get; init; }
        public long? StartMs { get; init; }
        public long? EndMs { get; init; }
        public List<LyricLine>? Words { get; init; }
    }
}
