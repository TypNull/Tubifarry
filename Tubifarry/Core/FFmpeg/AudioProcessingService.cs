using FFMpegCore;
using NLog;
using NzbDrone.Core.Music;
using Tubifarry.Core.Utilities;
using Tubifarry.Metadata.Lyrics.Converters;

namespace Tubifarry.Core.FFmpeg
{
    public interface IAudioProcessingService
    {
        bool IsFFmpegAvailable();
        Task<AudioFormat> DetectAudioFormatAsync(string filePath);
        Task<int?> GetAudioBitrateAsync(string filePath);
        Task<int?> GetAudioBitDepthAsync(string filePath);
        Task<bool> IsVideoContainerAsync(AudioFileContext file);
        Task<bool> ConvertToFormatAsync(AudioFileContext file, AudioFormat audioFormat, int? targetBitrate = null, int? targetBitDepth = null, bool useCBR = false);
        Task<bool> ExtractAudioFromVideoAsync(AudioFileContext file);
        Task<bool> DecryptAsync(AudioFileContext file, string decryptionKey, string? codec, CancellationToken token = default);
        Task<bool> EnsureCorrectFileExtensionAsync(AudioFileContext file);
        Task<bool> CreateLrcFileAsync(AudioFileContext file, CancellationToken token);
        bool EmbedMetadata(AudioFileContext file, Album albumInfo, Track trackInfo);
    }

    public sealed class AudioProcessingService(IFFmpegInstallation ffmpegInstallation, Logger logger) : IAudioProcessingService
    {
        private static readonly Dictionary<AudioFormat, string[]> BaseConversionParameters = new()
        {
            { AudioFormat.AAC,    new[] { "-codec:a aac", "-movflags +faststart", "-aac_coder twoloop" } },
            { AudioFormat.MP3,    new[] { "-codec:a libmp3lame" } },
            { AudioFormat.Opus,   new[] { "-codec:a libopus", "-vbr on", "-application audio", "-vn" } },
            { AudioFormat.Vorbis, new[] { "-codec:a libvorbis" } },
            { AudioFormat.FLAC,   new[] { "-codec:a flac", "-compression_level 8" } },
            { AudioFormat.ALAC,   new[] { "-codec:a alac" } },
            { AudioFormat.WAV,    new[] { "-codec:a pcm_s16le", "-ar 44100" } },
            { AudioFormat.MP4,    new[] { "-codec:a aac", "-movflags +faststart", "-aac_coder twoloop" } },
            { AudioFormat.AIFF,   new[] { "-codec:a pcm_s16be" } },
            { AudioFormat.OGG,    new[] { "-codec:a libvorbis" } },
            { AudioFormat.AMR,    new[] { "-codec:a libopencore_amrnb", "-ar 8000" } },
            { AudioFormat.WMA,    new[] { "-codec:a wmav2" } }
        };

        private static readonly Dictionary<AudioFormat, Func<int, string[]>> QualityParameters = new()
        {
            {
                AudioFormat.AAC,
                bitrate => bitrate < 256 ? [$"-b:a {bitrate}k"] : ["-q:a 2"]
            },
            {
                AudioFormat.MP3,
                bitrate => {
                    int qualityLevel = bitrate switch
                    {
                        >= 220 => 0,
                        >= 190 => 1,
                        >= 170 => 2,
                        >= 150 => 3,
                        >= 130 => 4,
                        >= 115 => 5,
                        >= 100 => 6,
                        >= 85 => 7,
                        >= 65 => 8,
                        _ => 9
                    };
                    return [$"-q:a {qualityLevel}"];
                }
            },
            {
                AudioFormat.Opus,
                bitrate => [$"-b:a {bitrate}k", "-compression_level 10"]
            },
            {
                AudioFormat.Vorbis,
                bitrate => [$"-q:a {AudioFormatHelper.MapBitrateToVorbisQuality(bitrate)}"]
            },
            { AudioFormat.MP4, bitrate => [$"-b:a {bitrate}k"] },
            {
                AudioFormat.OGG,
                bitrate => [$"-q:a {AudioFormatHelper.MapBitrateToVorbisQuality(bitrate)}"]
            },
            { AudioFormat.AMR, bitrate => [$"-ab {bitrate}k"] },
            { AudioFormat.WMA, bitrate => [$"-b:a {bitrate}k"] }
        };

        private static readonly Dictionary<AudioFormat, Func<int, string[]>> CBRQualityParameters = new()
        {
            { AudioFormat.MP3,  bitrate => [$"-b:a {bitrate}k"] },
            { AudioFormat.AAC,  bitrate => [$"-b:a {bitrate}k"] },
            { AudioFormat.Opus, bitrate => [$"-b:a {bitrate}k", "-vbr off"] },
            { AudioFormat.MP4,  bitrate => [$"-b:a {bitrate}k"] },
            { AudioFormat.AMR,  bitrate => [$"-ab {bitrate}k"] },
            { AudioFormat.WMA,  bitrate => [$"-b:a {bitrate}k"] }
        };

        private static readonly Dictionary<AudioFormat, Func<int, string[]>> BitDepthParameters = new()
        {
            {
                AudioFormat.FLAC,
                bitDepth => bitDepth switch
                {
                    16 => ["-sample_fmt s16"],
                    24 => ["-sample_fmt s32", "-bits_per_raw_sample 24"],
                    32 => ["-sample_fmt s32"],
                    _ => []
                }
            },
            {
                AudioFormat.WAV,
                bitDepth => bitDepth switch
                {
                    16 => ["-codec:a pcm_s16le"],
                    24 => ["-codec:a pcm_s24le"],
                    32 => ["-codec:a pcm_s32le"],
                    _ => []
                }
            },
            {
                AudioFormat.AIFF,
                bitDepth => bitDepth switch
                {
                    16 => ["-codec:a pcm_s16be"],
                    24 => ["-codec:a pcm_s24be"],
                    32 => ["-codec:a pcm_s32be"],
                    _ => []
                }
            }
        };

        private static readonly string[] ExtractionParameters =
        [
            "-codec:a copy",
            "-vn",
            "-movflags +faststart"
        ];

        private static readonly string[] VideoContainerFormats =
        [
            "matroska", "webm",
            "mov", "mp4", "m4a",
            "avi",
            "asf", "wmv", "wma",
            "flv", "f4v",
            "3gp", "3g2",
            "mxf",
            "ts", "m2ts"
        ];

        private static readonly HashSet<string> CoverArtCodecs = new(StringComparer.OrdinalIgnoreCase)
        {
            "mjpeg", "png", "bmp", "gif", "webp", "jpeg", "jpg", "tiff", "tif"
        };

        public bool IsFFmpegAvailable() => ffmpegInstallation.IsInstalled();

        public async Task<AudioFormat> DetectAudioFormatAsync(string filePath)
        {
            try
            {
                IMediaAnalysis analysis = await FFProbe.AnalyseAsync(filePath);
                string? codec = analysis.PrimaryAudioStream?.CodecName?.ToLowerInvariant();

                if (codec == null)
                {
                    logger.Debug("No audio stream found in file: {0}", filePath);
                    return AudioFormat.Unknown;
                }

                AudioFormat format = AudioFormatHelper.GetAudioFormatFromCodec(codec);
                logger.Trace("Detected codec '{0}' as format '{1}' for file: {2}", codec, format, filePath);
                return format;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failed to detect codec for file: {0}", filePath);
                return AudioFormat.Unknown;
            }
        }

        public async Task<int?> GetAudioBitrateAsync(string filePath)
        {
            try
            {
                IMediaAnalysis analysis = await FFProbe.AnalyseAsync(filePath);
                AudioStream? audioStream = analysis.PrimaryAudioStream;

                if (audioStream == null)
                    return null;

                return AudioFormatHelper.RoundToStandardBitrate((int)(audioStream.BitRate / 1000));
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failed to get bitrate: {0}", filePath);
                return null;
            }
        }

        public async Task<int?> GetAudioBitDepthAsync(string filePath)
        {
            try
            {
                IMediaAnalysis analysis = await FFProbe.AnalyseAsync(filePath);
                AudioStream? audioStream = analysis.PrimaryAudioStream;

                if (audioStream == null)
                    return null;

                return audioStream.BitDepth is > 0 ? audioStream.BitDepth : null;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failed to get bit depth: {0}", filePath);
                return null;
            }
        }

        public async Task<bool> IsVideoContainerAsync(AudioFileContext file)
        {
            try
            {
                IMediaAnalysis analysis = await FFProbe.AnalyseAsync(file.FilePath);

                bool hasRealVideo = analysis.VideoStreams.Any(stream =>
                    !CoverArtCodecs.Contains(stream.CodecName ?? "") &&
                    !(stream.Duration.TotalSeconds < 1 && stream.FrameRate <= 1));

                if (hasRealVideo)
                    return true;

                string formatName = analysis.Format.FormatName?.ToLowerInvariant() ?? "";
                return VideoContainerFormats.Any(container => formatName.Contains(container));
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failed to check file header: {0}", file.FilePath);
                return false;
            }
        }

        public async Task<bool> ConvertToFormatAsync(AudioFileContext file, AudioFormat audioFormat, int? targetBitrate = null, int? targetBitDepth = null, bool useCBR = false)
        {
            logger.Trace("Converting {0} to {1}{2}", Path.GetFileName(file.FilePath), audioFormat,
                targetBitrate.HasValue ? $" at {targetBitrate}kbps" : targetBitDepth.HasValue ? $" at {targetBitDepth}-bit" : "");

            if (!await ffmpegInstallation.EnsureReadyAsync())
                return false;

            if (!await ExtractAudioFromVideoAsync(file))
                return false;

            if (audioFormat == AudioFormat.Unknown)
                return true;

            if (!BaseConversionParameters.TryGetValue(audioFormat, out string[]? baseParameters))
                return false;

            string extension = AudioFormatHelper.GetFileExtensionForFormat(audioFormat);
            string finalOutputPath = Path.ChangeExtension(file.FilePath, extension);
            string tempOutputPath = Path.ChangeExtension(file.FilePath, $".converted{extension}");

            try
            {
                if (File.Exists(tempOutputPath))
                    File.Delete(tempOutputPath);

                byte[]? preservedCoverArt = file.AlbumCover?.Length > 0 ? file.AlbumCover : await TryExtractCoverArtAsync(file.FilePath);

                IMediaAnalysis analysis = await FFProbe.AnalyseAsync(file.FilePath);
                bool hasCoverArt = analysis.VideoStreams.Any(stream => CoverArtCodecs.Contains(stream.CodecName ?? ""));

                List<string> outputArguments = ["-map 0:a:0"];

                if (hasCoverArt)
                {
                    outputArguments.Add("-map 0:v:0");
                    outputArguments.Add("-c:v mjpeg -q:v 2 -disposition:v attached_pic");
                    logger.Trace("Detected attached picture stream, re-encoding as mjpeg with attached_pic disposition");
                }

                outputArguments.AddRange(baseParameters);
                outputArguments.AddRange(BuildQualityArguments(audioFormat, targetBitrate, targetBitDepth, useCBR));

                logger.Trace("Starting FFmpeg conversion");
                await RunConversionAsync(file.FilePath, tempOutputPath, outputArguments);

                ReplaceSourceFile(file, tempOutputPath, finalOutputPath);

                if (preservedCoverArt?.Length > 0)
                    TryReEmbedCoverArt(file, preservedCoverArt);

                return true;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failed to convert file to {0}: {1}", audioFormat, file.FilePath);
                return false;
            }
        }

        private static IEnumerable<string> BuildQualityArguments(AudioFormat audioFormat, int? targetBitrate, int? targetBitDepth, bool useCBR)
        {
            if (AudioFormatHelper.IsLossyFormat(audioFormat))
            {
                int bitrate = AudioFormatHelper.ClampBitrate(audioFormat, targetBitrate ?? AudioFormatHelper.GetDefaultBitrate(audioFormat));

                if (useCBR && CBRQualityParameters.TryGetValue(audioFormat, out Func<int, string[]>? cbrParameters))
                    return cbrParameters(bitrate);

                if (QualityParameters.TryGetValue(audioFormat, out Func<int, string[]>? vbrParameters))
                    return vbrParameters(bitrate);

                return [$"-b:a {bitrate}k"];
            }

            if (targetBitDepth.HasValue && BitDepthParameters.TryGetValue(audioFormat, out Func<int, string[]>? bitDepthParameters))
                return bitDepthParameters(targetBitDepth.Value);

            return [];
        }

        public async Task<bool> ExtractAudioFromVideoAsync(AudioFileContext file)
        {
            if (!await ffmpegInstallation.EnsureReadyAsync())
                return false;

            if (!await IsVideoContainerAsync(file))
                return await EnsureCorrectFileExtensionAsync(file);

            logger.Trace("Extracting audio from video file: {0}", Path.GetFileName(file.FilePath));

            try
            {
                IMediaAnalysis analysis = await FFProbe.AnalyseAsync(file.FilePath);
                AudioStream? audioStream = analysis.PrimaryAudioStream;

                if (audioStream == null)
                {
                    logger.Trace("No audio stream found in video file");
                    return false;
                }

                string codec = audioStream.CodecName.ToLowerInvariant();
                string extension = AudioFormatHelper.GetFileExtensionForCodec(codec);
                string finalOutputPath = Path.ChangeExtension(file.FilePath, extension);
                string tempOutputPath = Path.ChangeExtension(file.FilePath, $".extracted{extension}");

                if (File.Exists(tempOutputPath))
                    File.Delete(tempOutputPath);

                await RunConversionAsync(file.FilePath, tempOutputPath, ["-map 0:a:0", .. ExtractionParameters]);

                ReplaceSourceFile(file, tempOutputPath, finalOutputPath);
                await EnsureCorrectFileExtensionAsync(file);

                logger.Trace("Successfully extracted audio to {0}", Path.GetFileName(file.FilePath));
                return true;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failed to extract audio from video: {0}", file.FilePath);
                return false;
            }
        }

        private static bool IsMp4Container(string filePath)
        {
            try
            {
                using FileStream stream = File.OpenRead(filePath);
                Span<byte> header = stackalloc byte[12];
                if (stream.Read(header) < 12)
                    return false;
                return header[4] == (byte)'f' && header[5] == (byte)'t' && header[6] == (byte)'y' && header[7] == (byte)'p';
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> DecryptAsync(AudioFileContext file, string decryptionKey, string? codec, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(decryptionKey))
                return true;

            if (!await ffmpegInstallation.EnsureReadyAsync(token))
                return false;

            logger.Trace("Decrypting file: {0}", Path.GetFileName(file.FilePath));

            try
            {
                AudioFormat format = AudioFormatHelper.GetAudioFormatFromCodec(codec ?? "aac");
                string extension = AudioFormatHelper.GetFileExtensionForFormat(format);
                string outputPath = Path.ChangeExtension(file.FilePath, extension);
                string tempOutputPath = Path.ChangeExtension(file.FilePath, $".dec{extension}");

                if (File.Exists(tempOutputPath))
                    File.Delete(tempOutputPath);

                string inputArgs = IsMp4Container(file.FilePath)
                    ? $"-f mp4 -decryption_key {decryptionKey}"
                    : $"-decryption_key {decryptionKey}";

                await FFMpegArguments
                    .FromFileInput(file.FilePath, verifyExists: true, inputOptions => inputOptions.WithCustomArgument(inputArgs))
                    .OutputToFile(tempOutputPath, overwrite: true, outputOptions => outputOptions.WithCustomArgument("-c copy"))
                    .CancellableThrough(token)
                    .ProcessAsynchronously();

                ReplaceSourceFile(file, tempOutputPath, outputPath);

                logger.Trace("Successfully decrypted: {0}", Path.GetFileName(file.FilePath));
                return true;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failed to decrypt file: {0}", file.FilePath);
                return false;
            }
        }

        public async Task<bool> EnsureCorrectFileExtensionAsync(AudioFileContext file)
        {
            try
            {
                IMediaAnalysis analysis = await FFProbe.AnalyseAsync(file.FilePath);
                string codec = analysis.PrimaryAudioStream?.CodecName?.ToLowerInvariant() ?? string.Empty;
                if (string.IsNullOrEmpty(codec))
                    return false;

                string correctExtension = AudioFormatHelper.GetFileExtensionForCodec(codec);
                string currentExtension = Path.GetExtension(file.FilePath);

                if (!string.Equals(currentExtension, correctExtension, StringComparison.OrdinalIgnoreCase))
                {
                    string newPath = Path.ChangeExtension(file.FilePath, correctExtension);
                    logger.Trace("Correcting file extension from {0} to {1} for codec {2}", currentExtension, correctExtension, codec);
                    File.Move(file.FilePath, newPath);
                    file.FilePath = newPath;
                }

                return true;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failed to ensure correct file extension: {0}", file.FilePath);
                return false;
            }
        }

        public async Task<bool> CreateLrcFileAsync(AudioFileContext file, CancellationToken token)
        {
            if (file.Lyric == null || !file.Lyric.HasLineSync)
                return false;

            string lrcPath = Path.ChangeExtension(file.FilePath, ".lrc");

            try
            {
                string? lrcContent = new LrcConverter().Write(file.Lyric);
                if (string.IsNullOrEmpty(lrcContent))
                    return false;

                await File.WriteAllTextAsync(lrcPath, lrcContent, token);
                logger.Trace("Created LRC file with {0} synced lyrics", file.Lyric.Lines.Count);
                return true;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failed to create LRC file: {0}", lrcPath);
                return false;
            }
        }

        public bool EmbedMetadata(AudioFileContext file, Album albumInfo, Track trackInfo)
        {
            logger.Trace("Embedding metadata for track: {0}", trackInfo?.Title);

            try
            {
                using TagLib.File tagFile = TagLib.File.Create(file.FilePath);

                TagLib.Id3v2.Tag.DefaultVersion = (byte)(file.UseID3v2_3 ? 3 : 4);
                TagLib.Id3v2.Tag.ForceDefaultVersion = file.UseID3v2_3;

                if (!string.IsNullOrEmpty(trackInfo?.Title))
                    tagFile.Tag.Title = trackInfo.Title;

                if (trackInfo?.AbsoluteTrackNumber > 0)
                    tagFile.Tag.Track = (uint)trackInfo.AbsoluteTrackNumber;

                if (!string.IsNullOrEmpty(albumInfo?.Title))
                    tagFile.Tag.Album = albumInfo.Title;

                if (albumInfo?.ReleaseDate?.Year > 0)
                    tagFile.Tag.Year = (uint)albumInfo.ReleaseDate.Value.Year;

                if (albumInfo?.AlbumReleases?.Value?.FirstOrDefault()?.TrackCount > 0)
                    tagFile.Tag.TrackCount = (uint)albumInfo.AlbumReleases.Value[0].TrackCount;

                if (trackInfo?.MediumNumber > 0)
                    tagFile.Tag.Disc = (uint)trackInfo.MediumNumber;

                string? albumArtistName = albumInfo?.Artist?.Value?.Name;
                string? trackArtistName = trackInfo?.Artist?.Value?.Name;

                if (!string.IsNullOrEmpty(albumArtistName))
                    tagFile.Tag.AlbumArtists = [albumArtistName];

                if (!string.IsNullOrEmpty(trackArtistName))
                    tagFile.Tag.Performers = [trackArtistName];

                if (albumInfo?.AlbumReleases?.Value?.FirstOrDefault()?.Label?.Any() == true)
                    tagFile.Tag.Copyright = albumInfo.AlbumReleases.Value[0].Label.FirstOrDefault();

                if (albumInfo?.Genres?.Any() == true)
                {
                    string[] validGenres = albumInfo.Genres.Where(genre => !string.IsNullOrEmpty(genre)).ToArray();
                    if (validGenres.Length > 0)
                        tagFile.Tag.Genres = validGenres;
                }

                if (trackInfo?.Explicit == true)
                    tagFile.Tag.Comment = "EXPLICIT";

                if (!string.IsNullOrEmpty(trackInfo?.ForeignRecordingId) &&
                    tagFile.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3v2Tag)
                {
                    TagLib.Id3v2.UserTextInformationFrame mbFrame = TagLib.Id3v2.UserTextInformationFrame.Get(id3v2Tag, "MusicBrainz Recording Id", true);
                    mbFrame.Text = [trackInfo.ForeignRecordingId];
                }

                TryEmbedCoverArt(tagFile, file.AlbumCover);

                tagFile.Save();
                return true;
            }
            catch (TagLib.CorruptFileException ex)
            {
                logger.Error(ex, "File is corrupted or has incorrect extension: {0}", file.FilePath);
                return false;
            }
            catch (TagLib.UnsupportedFormatException ex)
            {
                logger.Error(ex, "File format does not support metadata embedding: {0}", file.FilePath);
                return false;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failed to embed metadata in track: {0}", file.FilePath);
                return false;
            }
        }

        private static async Task RunConversionAsync(string inputPath, string outputPath, IEnumerable<string> outputArguments)
        {
            await FFMpegArguments
                .FromFileInput(inputPath)
                .OutputToFile(outputPath, overwrite: true, outputOptions =>
                {
                    foreach (string argument in outputArguments)
                        outputOptions.WithCustomArgument(argument);
                })
                .ProcessAsynchronously();
        }

        private static void ReplaceSourceFile(AudioFileContext file, string tempOutputPath, string finalOutputPath)
        {
            if (File.Exists(file.FilePath))
                File.Delete(file.FilePath);

            File.Move(tempOutputPath, finalOutputPath, true);
            file.FilePath = finalOutputPath;
        }

        private async Task<byte[]?> TryExtractCoverArtAsync(string filePath)
        {
            try
            {
                using TagLib.File tagFile = TagLib.File.Create(filePath);
                byte[]? embeddedCover = tagFile.Tag.Pictures?.FirstOrDefault()?.Data?.Data;
                if (embeddedCover?.Length > 0)
                    return embeddedCover;
            }
            catch { }

            try
            {
                IMediaAnalysis analysis = await FFProbe.AnalyseAsync(filePath);
                bool hasCoverStream = analysis.VideoStreams.Any(stream => CoverArtCodecs.Contains(stream.CodecName ?? ""));

                if (!hasCoverStream)
                    return null;

                string tempCoverPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.jpg");
                try
                {
                    await FFMpegArguments
                        .FromFileInput(filePath)
                        .OutputToFile(tempCoverPath, overwrite: true, outputOptions => outputOptions.WithCustomArgument("-an -vcodec copy"))
                        .ProcessAsynchronously();

                    if (File.Exists(tempCoverPath))
                        return await File.ReadAllBytesAsync(tempCoverPath);
                }
                finally
                {
                    if (File.Exists(tempCoverPath))
                        File.Delete(tempCoverPath);
                }
            }
            catch { }

            return null;
        }

        private void TryReEmbedCoverArt(AudioFileContext file, byte[] coverArt)
        {
            try
            {
                using TagLib.File tagFile = TagLib.File.Create(file.FilePath);
                tagFile.Tag.Pictures =
                [
                    new TagLib.Picture(new TagLib.ByteVector(coverArt))
                    {
                        Type = TagLib.PictureType.FrontCover,
                        Description = "Album Cover"
                    }
                ];
                tagFile.Save();
                logger.Trace("Re-embedded cover art into converted file");
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Failed to re-embed cover art after conversion, cover art may be missing");
            }
        }

        private void TryEmbedCoverArt(TagLib.File tagFile, byte[]? coverArt)
        {
            try
            {
                if (coverArt?.Length > 0)
                {
                    tagFile.Tag.Pictures =
                    [
                        new TagLib.Picture(new TagLib.ByteVector(coverArt))
                        {
                            Type = TagLib.PictureType.FrontCover,
                            Description = "Album Cover"
                        }
                    ];
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failed to embed album cover");
            }
        }
    }
}
