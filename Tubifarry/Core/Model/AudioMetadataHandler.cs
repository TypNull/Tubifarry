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
            _logger = NzbDroneLogger.GetLogger(this);
        }

        /// <summary>
        /// Base codec parameters that don't change with bitrate settings
        /// </summary>
        private static readonly Dictionary<AudioFormat, string[]> BaseConversionParameters = new()
        {
            { AudioFormat.AAC,    new[] { "-codec:a aac", "-movflags +faststart", "-aac_coder twoloop" } },
            { AudioFormat.MP3,    new[] { "-codec:a libmp3lame" } },
            { AudioFormat.Opus,   new[] { "-codec:a libopus", "-vbr on", "-application audio" } },
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

        /// <summary>
        /// Format-specific bitrate/quality parameter templates
        /// </summary>
        private static readonly Dictionary<AudioFormat, Func<int, string[]>> QualityParameters = new()
        {
            {
                AudioFormat.AAC,
                bitrate => bitrate < 256
                    ? new[] { $"-b:a {bitrate}k" }
                    : new[] { "-q:a 2" } // 2 is highest quality for AAC
            },

            {
                AudioFormat.MP3,
                bitrate => {
                    int qualityLevel = bitrate switch {
                        >= 220 => 0,   // V0 (~220-260kbps avg)
                        >= 190 => 1,   // V1 (~190-250kbps)
                        >= 170 => 2,   // V2 (~170-210kbps)
                        >= 150 => 3,   // V3 (~150-195kbps)
                        >= 130 => 4,   // V4 (~130-175kbps)
                        >= 115 => 5,   // V5 (~115-155kbps)
                        >= 100 => 6,   // V6 (~100-140kbps)
                        >= 85 => 7,    // V7 (~85-125kbps)
                        >= 65 => 8,    // V8 (~65-105kbps)
                        _ => 9         // V9 (~45-85kbps)
                    };
                    return new[] { $"-q:a {qualityLevel}" };
                }
            },

            {
                AudioFormat.Opus,
                bitrate => new[] {
                    $"-b:a {bitrate}k",
                    "-compression_level 10"
                }
            },

            {
                AudioFormat.Vorbis,
                bitrate => new[] { $"-q:a {AudioFormatHelper.MapBitrateToVorbisQuality(bitrate)}" }
            },

            { AudioFormat.MP4, bitrate => new[] { $"-b:a {bitrate}k" } },
            {
                AudioFormat.OGG,
                bitrate => new[] { $"-q:a {AudioFormatHelper.MapBitrateToVorbisQuality(bitrate)}" }
            },
            { AudioFormat.AMR, bitrate => new[] { $"-ab {bitrate}k" } },
            { AudioFormat.WMA, bitrate => new[] { $"-b:a {bitrate}k" } }
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

        /// <summary>
        /// Converts audio to the specified format with optional bitrate control.
        /// </summary>
        /// <param name="audioFormat">Target audio format</param>
        /// <param name="targetBitrate">Optional target bitrate in kbps</param>
        /// <returns>True if conversion succeeded, false otherwise</returns>
        public async Task<bool> TryConvertToFormatAsync(AudioFormat audioFormat, int? targetBitrate = null)
        {
            _logger?.Trace($"Converting {Path.GetFileName(TrackPath)} to {audioFormat}" +
                          (targetBitrate.HasValue ? $" at {targetBitrate}kbps" : ""));

            if (!CheckFFmpegInstalled())
                return false;

            if (!await TryExtractAudioFromVideoAsync())
                return false;

            _logger?.Trace($"Looking up audio format: {audioFormat}");

            if (audioFormat == AudioFormat.Unknown)
                return true;

            if (!BaseConversionParameters.ContainsKey(audioFormat))
                return false;

            string finalOutputPath = Path.ChangeExtension(TrackPath, AudioFormatHelper.GetFileExtensionForFormat(audioFormat));
            string tempOutputPath = Path.ChangeExtension(TrackPath, $".converted{AudioFormatHelper.GetFileExtensionForFormat(audioFormat)}");

            try
            {
                if (File.Exists(tempOutputPath))
                    File.Delete(tempOutputPath);

                IConversion conversion = await FFmpeg.Conversions.FromSnippet.Convert(TrackPath, tempOutputPath);

                foreach (string parameter in BaseConversionParameters[audioFormat])
                    conversion.AddParameter(parameter);

                if (AudioFormatHelper.IsLossyFormat(audioFormat) && QualityParameters.ContainsKey(audioFormat))
                {
                    int bitrate = targetBitrate ?? AudioFormatHelper.GetDefaultBitrate(audioFormat);
                    bitrate = AudioFormatHelper.ClampBitrate(audioFormat, bitrate);

                    string[] qualityParams = QualityParameters[audioFormat](bitrate);
                    foreach (string param in qualityParams)
                        conversion.AddParameter(param);

                    _logger?.Trace($"Applied quality parameters for {audioFormat}: {string.Join(", ", qualityParams)}");
                }

                _logger?.Trace($"Starting FFmpeg conversion");
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
                    {
                        _logger?.Trace($"Detected {containerType} video container via signature");
                        return true;
                    }
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

            _logger?.Trace($"Extracting audio from video file: {Path.GetFileName(TrackPath)}");

            try
            {
                IMediaInfo mediaInfo = await FFmpeg.GetMediaInfo(TrackPath);
                IAudioStream? audioStream = mediaInfo.AudioStreams.FirstOrDefault();

                if (audioStream == null)
                {
                    _logger?.Trace("No audio stream found in video file");
                    return false;
                }

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
                _logger?.Trace($"Successfully extracted audio to {Path.GetFileName(TrackPath)}");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to extract audio from video: {TrackPath}");
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

                string lrcPath = Path.ChangeExtension(TrackPath, ".lrc");
                await File.WriteAllTextAsync(lrcPath, lrcContent, token);
                _logger?.Trace($"Created LRC file with {Lyric.SyncedLyrics.Count} synced lyrics");
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to create LRC file: {Path.ChangeExtension(TrackPath, ".lrc")}");
                return false;
            }
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
                    _logger?.Trace($"Correcting file extension from {currentExtension} to {correctExtension} for codec {codec}");
                    File.Move(TrackPath, newPath);
                    TrackPath = newPath;
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to ensure correct file extension: {TrackPath}");
                return false;
            }
        }

        public bool TryEmbedMetadata(AlbumInfo albumInfo, AlbumSongInfo trackInfo, ReleaseInfo releaseInfo)
        {
            _logger?.Trace($"Embedding metadata for track: {trackInfo.Name}");

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
                    _logger?.Error(ex, "Failed to embed album cover");
                }

                if (!string.IsNullOrEmpty(Lyric?.PlainLyrics))
                    file.Tag.Lyrics = Lyric.PlainLyrics;

                file.Save();
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to embed metadata in track: {TrackPath}");
                return false;
            }
        }

        /// <summary>
        /// Checks if the specified audio format is supported for encoding by FFmpeg.
        /// </summary>
        /// <param name="format">The audio format to check</param>
        /// <returns>True if the format can be used as a conversion target, false otherwise</returns>
        public static bool IsTargetFormatSupportedForEncoding(AudioFormat format) => BaseConversionParameters.ContainsKey(format);


        /// <summary>
        /// Gets the actual audio codec from a file using FFmpeg and returns the corresponding AudioFormat.
        /// </summary>
        /// <param name="filePath">Path to the audio file</param>
        /// <returns>AudioFormat enum value or AudioFormat.Unknown if codec is not supported or detection fails</returns>
        public static async Task<AudioFormat> GetSupportedCodecAsync(string filePath)
        {
            try
            {
                IMediaInfo mediaInfo = await FFmpeg.GetMediaInfo(filePath);
                IAudioStream? audioStream = mediaInfo.AudioStreams.FirstOrDefault();

                if (audioStream == null)
                {
                    NzbDroneLogger.GetLogger(typeof(AudioMetadataHandler)).Debug("No audio stream found in file: {0}", filePath);
                    return AudioFormat.Unknown;
                }

                string codec = audioStream.Codec.ToLower();
                AudioFormat format = AudioFormatHelper.GetAudioFormatFromCodec(codec);

                NzbDroneLogger.GetLogger(typeof(AudioMetadataHandler)).Trace("Detected codec '{0}' as format '{1}' for file: {2}", codec, format, filePath);
                return format;
            }
            catch (Exception ex)
            {
                NzbDroneLogger.GetLogger(typeof(AudioMetadataHandler)).Error(ex, "Failed to detect codec for file: {0}", filePath);
                return AudioFormat.Unknown;
            }
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
                {
                    isInstalled = true;
                }
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

            if (!isInstalled)
                NzbDroneLogger.GetLogger(typeof(AudioMetadataHandler)).Trace("FFmpeg not found in configured path or system PATH");

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

                // Windows PE
                if (magicNumber[0] == 0x4D && magicNumber[1] == 0x5A)
                    return true;

                // Linux ELF
                if (magicNumber[0] == 0x7F && magicNumber[1] == 0x45 &&
                    magicNumber[2] == 0x4C && magicNumber[3] == 0x46)
                    return true;

                // macOS Mach-O (32-bit: 0xFEEDFACE, 64-bit: 0xFEEDFACF)
                if (magicNumber[0] == 0xFE && magicNumber[1] == 0xED &&
                    magicNumber[2] == 0xFA &&
                    (magicNumber[3] == 0xCE || magicNumber[3] == 0xCF))
                    return true;

                // Universal Binary (fat_header)
                if (magicNumber[0] == 0xCA && magicNumber[1] == 0xFE &&
                    magicNumber[2] == 0xBA && magicNumber[3] == 0xBE)
                    return true;
            }
            catch { }
            return false;
        }

        public static void ResetFFmpegInstallationCheck() => _isFFmpegInstalled = null;

        public static Task InstallFFmpeg(string path)
        {
            NzbDroneLogger.GetLogger(typeof(AudioMetadataHandler)).Trace($"Installing FFmpeg to: {path}");
            ResetFFmpegInstallationCheck();
            FFmpeg.SetExecutablesPath(path);
            return CheckFFmpegInstalled() ? Task.CompletedTask : FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, path);
        }
    }
}