using Tubifarry.Core.Records;

namespace Tubifarry.Core.FFmpeg
{
    public sealed class AudioFileContext(string filePath)
    {
        public string FilePath { get; internal set; } = filePath;

        public Lyric? Lyric { get; set; }

        public byte[]? AlbumCover { get; set; }

        public bool UseID3v2_3 { get; set; }
    }
}
