using FluentValidation.Results;
using NLog;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using Tubifarry.Core.Python;

namespace Tubifarry.Metadata.Beets
{
    public class BeetsMetadata(IBeetsService beetsService, Logger logger) : MetadataBase<BeetsSettings>, IMetadata
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(10);

        public override string Name => "Beets";

        public new ValidationResult Test()
        {
            try
            {
                using CancellationTokenSource timeout = new(TestTimeout);
                string version = beetsService.CheckAsync(Settings, timeout.Token).GetAwaiter().GetResult();
                logger.Info("beets {0} is ready", version);
                return new ValidationResult();
            }
            catch (PythonWorkerException ex)
            {
                logger.Warn("beets could not be loaded: {0}\n{1}", ex.Message, ex.PythonTraceback);
                return new ValidationResult([new ValidationFailure(nameof(Settings.ConfigPath), $"beets could not start with these settings: {ex.Message}")]);
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Preparing beets failed");
                return new ValidationResult([new ValidationFailure(nameof(Settings.InstallDirectory), ex.Message)]);
            }
        }

        public override MetadataFile? FindMetadataFile(Artist artist, string path) => null;

        public override MetadataFileResult? ArtistMetadata(Artist artist) => null;

        public override MetadataFileResult? AlbumMetadata(Artist artist, Album album, string albumPath) => null;

        public override MetadataFileResult? TrackMetadata(Artist artist, TrackFile trackFile) => null;

        public override List<ImageFileResult> ArtistImages(Artist artist) => [];

        public override List<ImageFileResult> AlbumImages(Artist artist, Album album, string albumFolder) => [];

        public override List<ImageFileResult> TrackImages(Artist artist, TrackFile trackFile) => [];
    }
}
