using FluentValidation.Results;
using NLog;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using Tubifarry.Core.FFmpeg;

namespace Tubifarry.Metadata.FFmpeg
{
    public class FFmpegMetadata(IFFmpegInstallation ffmpegInstallation, Logger logger) : MetadataBase<FFmpegSettings>, IMetadata
    {
        public override string Name => "FFmpeg";

        public new ValidationResult Test()
        {
            List<ValidationFailure> failures = [];

            string? installDirectory = string.IsNullOrWhiteSpace(Settings.InstallDirectory) ? null : Settings.InstallDirectory;

            if (installDirectory != null)
                ffmpegInstallation.UseExecutablesDirectory(installDirectory);
            else
                ffmpegInstallation.Reset();

            if (ffmpegInstallation.IsInstalled())
            {
                logger.Debug("FFmpeg found at {0}", ffmpegInstallation.ExecutablesDirectory);
                return new ValidationResult(failures);
            }

            if (!Settings.AutoDownload)
            {
                failures.Add(new ValidationFailure(nameof(Settings.InstallDirectory),
                    "No usable FFmpeg binary was found in the install directory, plugin directory, FFMPEG variable or PATH. Enable 'Download Automatically' or install FFmpeg manually."));
                return new ValidationResult(failures);
            }

            try
            {
                ffmpegInstallation.EnsureInstalledAsync(installDirectory).GetAwaiter().GetResult();
                logger.Info("FFmpeg is ready at {0}", ffmpegInstallation.ExecutablesDirectory);
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "FFmpeg installation failed");
                failures.Add(new ValidationFailure(nameof(Settings.InstallDirectory), $"FFmpeg installation failed: {ex.Message}"));
            }

            return new ValidationResult(failures);
        }

        public override MetadataFile FindMetadataFile(Artist artist, string path) => default!;

        public override MetadataFileResult ArtistMetadata(Artist artist) => default!;

        public override MetadataFileResult AlbumMetadata(Artist artist, Album album, string albumPath) => default!;

        public override MetadataFileResult TrackMetadata(Artist artist, TrackFile trackFile) => default!;

        public override List<ImageFileResult> ArtistImages(Artist artist) => default!;

        public override List<ImageFileResult> AlbumImages(Artist artist, Album album, string albumFolder) => default!;

        public override List<ImageFileResult> TrackImages(Artist artist, TrackFile trackFile) => default!;
    }
}
