using NLog;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Parser.Model;
using Tubifarry.Core.Records;
using Tubifarry.Core.Utilities;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;
using YouTubeMusicAPI.Models.Info;

namespace Tubifarry.Core.Model
{
    internal class AudioMetadataHandler
    {
        private readonly Logger? _logger;
        private static bool? _isFFmpegInstalled = null;

        public string TrackPath { get; private set; }
        public Lyric? Lyric { get; set; }
        public byte[]? AlbumCover { get; set; }
        public bool UseID3v2_3 { get; set; }


        public AudioMetadataHandler(string originalPath)
        {
            TrackPath = originalPath;
            _logger = NzbDroneLogger.GetLogger(this); ;
        }

        private static readonly Dictionary<AudioFormat, string[]> ConversionParameters = new()
{
    { AudioFormat.AAC,    new[] { "-codec:a aac", "-q:a 0", "-movflags +faststart" } },
    { AudioFormat.MP3,    new[] { "-codec:a libmp3lame", "-q:a 0", "-preset insane" } },
    { AudioFormat.Opus,   new[] { "-codec:a libopus", "-vbr on", "-compression_level 10", "-application audio" } },
    { AudioFormat.Vorbis, new[] { "-codec:a libvorbis", "-q:a 7" } },
    { AudioFormat.FLAC,   new[] { "-codec:a flac" } },
    { AudioFormat.ALAC,   new[] { "-codec:a alac" } },
    { AudioFormat.WAV,    new[] { "-codec:a pcm_s16le" } },
    { AudioFormat.MP4,    new[] { "-codec:a aac", "-q:a 0", "-movflags +faststart" } },
    { AudioFormat.AIFF,   new[] { "-codec:a pcm_s16le" } },
    { AudioFormat.OGG,    new[] { "-codec:a libvorbis", "-q:a 7" } },
    { AudioFormat.AMR,    new[] { "-codec:a libopencore_amrnb", "-ar 8000", "-ab 12.2k" } },
    { AudioFormat.WMA,    new[] { "-codec:a wmav2", "-b:a 192k" } }
};


        private static readonly string[] ExtractionParameters = new[]
{
            "-codec:a copy",
            "-vn",
            "-movflags +faststart"
        };

        private static readonly Dictionary<string, byte[]> VideoSignatures = new()
        {
            { "MP4", new byte[] { 0x66, 0x74, 0x79, 0x70 } }, // MP4 (ftyp)
            { "AVI", new byte[] { 0x52, 0x49, 0x46, 0x46 } }, // AVI (RIFF)
            { "MKV", new byte[] { 0x1A, 0x45, 0xDF, 0xA3 } }, // MKV (EBML)
        };

        public async Task<bool> TryConvertToFormatAsync(AudioFormat audioFormat)
        {
            if (!CheckFFmpegInstalled())
                return false;

            if (!await TryExtractAudioFromVideoAsync())
                return false;

            if (audioFormat == AudioFormat.Unknown)
                return true;

            if (!ConversionParameters.ContainsKey(audioFormat))
                return false;

            string finalOutputPath = Path.ChangeExtension(TrackPath, AudioFormatHelper.GetFileExtensionForFormat(audioFormat));
            string tempOutputPath = Path.ChangeExtension(TrackPath, $".converted{AudioFormatHelper.GetFileExtensionForFormat(audioFormat)}");

            try
            {
                if (File.Exists(tempOutputPath))
                    File.Delete(tempOutputPath);

                IConversion conversion = await FFmpeg.Conversions.FromSnippet.Convert(TrackPath, tempOutputPath);
                foreach (string parameter in ConversionParameters[audioFormat])
                    conversion.AddParameter(parameter);

                await conversion.Start();

                if (File.Exists(TrackPath))
                    File.Delete(TrackPath);

                File.Move(tempOutputPath, finalOutputPath, true);
                TrackPath = finalOutputPath;
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to convert file to {audioFormat}: {TrackPath}");
                return false;
            }
        }

        public async Task<bool> IsVideoContainerAsync()
        {
            try
            {
                IMediaInfo mediaInfo = await FFmpeg.GetMediaInfo(TrackPath);
                if (mediaInfo.VideoStreams.Any())
                    return true;

                byte[] header = new byte[8];
                await using (FileStream stream = new(TrackPath, FileMode.Open, FileAccess.Read))
                {
                    await stream.ReadAsync(header);
                }

                foreach (KeyValuePair<string, byte[]> kvp in VideoSignatures)
                {
                    string containerType = kvp.Key;
                    byte[] signature = kvp.Value;
                    if (header.Skip(4).Take(signature.Length).SequenceEqual(signature))
                        return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to check file header: {TrackPath}");
                return false;
            }
        }

        public async Task<bool> TryExtractAudioFromVideoAsync()
        {
            if (!CheckFFmpegInstalled())
                return false;

            bool isVideo = await IsVideoContainerAsync();
            if (!isVideo)
                return true;

            try
            {
                IMediaInfo mediaInfo = await FFmpeg.GetMediaInfo(TrackPath);
                IAudioStream? audioStream = mediaInfo.AudioStreams.FirstOrDefault();

                if (audioStream == null)
                    return false;

                string codec = audioStream.Codec.ToLower();
                string finalOutputPath = Path.ChangeExtension(TrackPath, AudioFormatHelper.GetFileExtensionForCodec(codec));
                string tempOutputPath = Path.ChangeExtension(TrackPath, $".extracted{AudioFormatHelper.GetFileExtensionForCodec(codec)}");

                if (File.Exists(tempOutputPath))
                    File.Delete(tempOutputPath);

                IConversion conversion = await FFmpeg.Conversions.FromSnippet.ExtractAudio(TrackPath, tempOutputPath);
                foreach (string parameter in ExtractionParameters)
                    conversion.AddParameter(parameter);

                await conversion.Start();

                if (File.Exists(TrackPath))
                    File.Delete(TrackPath);

                File.Move(tempOutputPath, finalOutputPath, true);
                TrackPath = finalOutputPath;
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to extract audio from MP4: {TrackPath}");
                return false;
            }
        }

        public async Task<bool> TryCreateLrcFileAsync(CancellationToken token)
        {
            if (Lyric?.SyncedLyrics == null)
                return false;
            try
            {
                string lrcContent = string.Join(Environment.NewLine, Lyric.SyncedLyrics
                    .Where(lyric => !string.IsNullOrEmpty(lyric.LrcTimestamp) && !string.IsNullOrEmpty(lyric.Line))
                    .Select(lyric => $"{lyric.LrcTimestamp} {lyric.Line}"));
                await File.WriteAllTextAsync(Path.ChangeExtension(TrackPath, ".lrc"), lrcContent, token);
            }
            catch (Exception) { return false; }
            return true;
        }

        /// <summary>
        /// Ensures the file extension matches the actual audio codec.
        /// </summary>
        /// <returns>True if the file extension is correct or was successfully corrected; otherwise, false.</returns>
        public async Task<bool> EnsureFileExtAsync()
        {
            try
            {
                IMediaInfo mediaInfo = await FFmpeg.GetMediaInfo(TrackPath);
                string codec = mediaInfo.AudioStreams.FirstOrDefault()?.Codec.ToLower() ?? string.Empty;
                if (string.IsNullOrEmpty(codec))
                    return false;

                string correctExtension = AudioFormatHelper.GetFileExtensionForCodec(codec);
                string currentExtension = Path.GetExtension(TrackPath);

                if (!string.Equals(currentExtension, correctExtension, StringComparison.OrdinalIgnoreCase))
                {
                    string newPath = Path.ChangeExtension(TrackPath, correctExtension);
                    File.Move(TrackPath, newPath);
                    TrackPath = newPath;
                }
                return true;
            }
            catch (Exception) { return false; }
        }


        public bool TryEmbedMetadata(AlbumInfo albumInfo, AlbumSongInfo trackInfo, ReleaseInfo releaseInfo)
        {
            try
            {
                using TagLib.File file = TagLib.File.Create(TrackPath);

                if (UseID3v2_3)
                {
                    TagLib.Id3v2.Tag.DefaultVersion = 3;
                    TagLib.Id3v2.Tag.ForceDefaultVersion = true;
                }
                else
                {
                    TagLib.Id3v2.Tag.DefaultVersion = 4;
                    TagLib.Id3v2.Tag.ForceDefaultVersion = false;
                }

                if (!string.IsNullOrEmpty(trackInfo.Name))
                    file.Tag.Title = trackInfo.Name;

                if (trackInfo.SongNumber.HasValue)
                    file.Tag.Track = (uint)trackInfo.SongNumber.Value;

                if (albumInfo.SongCount > 0)
                    file.Tag.TrackCount = (uint)albumInfo.SongCount;

                if (!string.IsNullOrEmpty(releaseInfo.Album))
                    file.Tag.Album = releaseInfo.Album;

                if (releaseInfo.PublishDate.Year > 0)
                    file.Tag.Year = (uint)releaseInfo.PublishDate.Year;

                if (!string.IsNullOrEmpty(releaseInfo.Artist))
                {
                    file.Tag.AlbumArtists = albumInfo.Artists.Select(x => x.Name).ToArray();
                    file.Tag.Performers = new[] { releaseInfo.Artist };
                }

                if (trackInfo.IsExplicit)
                    file.Tag.Comment = "EXPLICIT";

                try
                {
                    if (AlbumCover != null)
                        file.Tag.Pictures = new TagLib.IPicture[] { new TagLib.Picture(new TagLib.ByteVector(AlbumCover)) };
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, $"Failed to embed cover in track: {TrackPath}");
                }

                if (!string.IsNullOrEmpty(Lyric?.PlainLyrics))
                    file.Tag.Lyrics = Lyric.PlainLyrics;

                file.Save();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to embed metadata in track: {TrackPath}");
                return false;
            }
            return true;
        }


        public static bool CheckFFmpegInstalled()
        {
            if (_isFFmpegInstalled.HasValue)
                return _isFFmpegInstalled.Value;

            bool isInstalled = false;

            if (!string.IsNullOrEmpty(FFmpeg.ExecutablesPath) && Directory.Exists(FFmpeg.ExecutablesPath))
            {
                string[] ffmpegPatterns = new[] { "ffmpeg", "ffmpeg.exe", "ffmpeg.bin" };
                string[] files = Directory.GetFiles(FFmpeg.ExecutablesPath);
                if (files.Any(file => ffmpegPatterns.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase) && IsExecutable(file)))
                    isInstalled = true;
            }

            if (!isInstalled)
            {
                foreach (string path in Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? Array.Empty<string>())
                {
                    if (Directory.Exists(path))
                    {
                        string[] ffmpegPatterns = new[] { "ffmpeg", "ffmpeg.exe", "ffmpeg.bin" };
                        string[] files = Directory.GetFiles(path);

                        if (files.Any(file => ffmpegPatterns.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase) && IsExecutable(file)))
                        {
                            isInstalled = true;
                            break;
                        }
                    }
                }
            }

            _isFFmpegInstalled = isInstalled;
            return isInstalled;
        }


        private static bool IsExecutable(string filePath)
        {
            try
            {
                using FileStream stream = File.OpenRead(filePath);
                byte[] magicNumber = new byte[4];
                stream.Read(magicNumber, 0, 4);

                if (magicNumber[0] == 0x4D && magicNumber[1] == 0x5A)
                    return true;
                if (magicNumber[0] == 0x7F && magicNumber[1] == 0x45 && magicNumber[2] == 0x4C && magicNumber[3] == 0x46)
                    return true;
                if (magicNumber[0] == 0xCF && magicNumber[1] == 0xFA && magicNumber[2] == 0xED && magicNumber[3] == 0xFE)
                    return true;
                if (magicNumber[0] == 0xCE && magicNumber[1] == 0xFA && magicNumber[2] == 0xED && magicNumber[3] == 0xFE)
                    return true;
            }
            catch
            {
            }

            return false;
        }

        public static void ResetFFmpegInstallationCheck() => _isFFmpegInstalled = null;

        public static Task InstallFFmpeg(string path)
        {
            ResetFFmpegInstallationCheck();
            FFmpeg.SetExecutablesPath(path);
            return CheckFFmpegInstalled() ? Task.CompletedTask : FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, path);
        }
    }
}