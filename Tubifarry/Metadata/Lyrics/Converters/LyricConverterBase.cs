using Tubifarry.Core.Records;

namespace Tubifarry.Metadata.Lyrics.Converters
{
    public abstract class LyricConverterBase
    {
        public abstract Lyric? Read(string content);
        public abstract string? Write(Lyric lyric);
    }
}
