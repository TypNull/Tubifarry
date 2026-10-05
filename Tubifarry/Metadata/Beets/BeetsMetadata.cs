using FluentValidation.Results;
using NLog;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Music;
using Tubifarry.Core.Python;
using Tubifarry.Metadata.ScheduledTasks;

namespace Tubifarry.Metadata.Beets
{
    public sealed class BeetsSyncCommand : Command
    {
        public override bool SendUpdatesToClient => true;

        public override bool UpdateScheduledTask => true;

        public override string CompletionMessage => "beets sync completed";
    }

    public class BeetsMetadata(IBeetsService beetsService, IArtistService artistService, IAlbumService albumService, IMediaFileService mediaFileService, Logger logger)
        : ScheduledTaskBase<BeetsSettings>, IExecute<BeetsSyncCommand>
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(10);

        public override string Name => "Beets";

        public override Type CommandType => typeof(BeetsSyncCommand);

        public override int IntervalMinutes => (Settings?.SyncInterval ?? 0) * 60;

        public override ValidationResult Test()
        {
            if (Settings == null)
                return new ValidationResult();

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

        public void Execute(BeetsSyncCommand message)
        {
            BeetsSettings? settings = Settings;
            if (settings == null || Definition?.Enable != true)
                return;

            try
            {
                SyncAsync(settings).GetAwaiter().GetResult();
            }
            catch (PythonWorkerException ex)
            {
                logger.Error("beets sync failed: {0}\n{1}", ex.Message, ex.PythonTraceback);
            }
        }

        private async Task SyncAsync(BeetsSettings settings)
        {
            if (settings.CatchUpMissingAlbums)
                await CatchUpAsync(settings);

            if (!settings.AllowRetagging)
                return;

            BeetsSyncResult result = await beetsService.SyncAsync(settings);
            logger.Info("beets retag: {0} track(s) checked against MusicBrainz, {1} updated, {2} skipped because they are hardlinked", result.Items, result.Written, result.Hardlinked);
        }

        private async Task CatchUpAsync(BeetsSettings settings)
        {
            IReadOnlySet<string> known = await beetsService.GetKnownFoldersAsync(settings);
            Dictionary<BeetsImportOutcome, int> outcomes = [];
            int present = 0;
            int skipped = 0;

            foreach (Artist artist in artistService.GetAllArtists())
            {
                Dictionary<int, string> titles = albumService.GetAlbumsByArtist(artist.Id).ToDictionary(album => album.Id, album => album.Title);

                foreach (IGrouping<int, TrackFile> files in mediaFileService.GetFilesByArtist(artist.Id).GroupBy(file => file.AlbumId))
                {
                    BeetsAlbumFolder album = BeetsAlbumFolder.Resolve(files.Select(file => file.Path), artist.Path);

                    if (album.Problem != BeetsAlbumFolderProblem.None)
                    {
                        skipped++;
                        continue;
                    }

                    if (known.Contains(Path.TrimEndingDirectorySeparator(album.Folder!)))
                    {
                        present++;
                        continue;
                    }

                    BeetsImportOutcome outcome = await beetsService.ImportAlbumAsync(settings, album.Folder!, titles.GetValueOrDefault(files.Key, album.Folder!), settings.AllowRetagging);
                    outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
                }
            }

            logger.Info("beets catch-up: {0} album(s) imported, {1} already known, {2} without a confident match, {3} skipped because they share a folder or lie outside the artist folder",
                outcomes.GetValueOrDefault(BeetsImportOutcome.Imported),
                present + outcomes.GetValueOrDefault(BeetsImportOutcome.AlreadyPresent),
                outcomes.GetValueOrDefault(BeetsImportOutcome.NoMatch),
                skipped + outcomes.GetValueOrDefault(BeetsImportOutcome.Missing));
        }
    }
}
