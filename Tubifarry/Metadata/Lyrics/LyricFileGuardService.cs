using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Lyrics;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging;
using NzbDrone.Core.Messaging.Events;
using System.Collections.Concurrent;

namespace Tubifarry.Metadata.Lyrics
{
    public class LyricFileGuardService : IHandle<TrackFileDeletedEvent>, IHandle<TrackFileAddedEvent>
    {
        private static readonly TimeSpan PendingLifetime = TimeSpan.FromHours(1);

        private readonly ILyricFileService _lyricFileService;
        private readonly IMetadataFileService _metadataFileService;
        private readonly IExtraFileService<OtherExtraFile> _otherExtraFileService;
        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;

        private readonly ConcurrentDictionary<string, PendingLyrics> _pendingRelinks = new();

        public LyricFileGuardService(
            ILyricFileService lyricFileService,
            IMetadataFileService metadataFileService,
            IExtraFileService<OtherExtraFile> otherExtraFileService,
            IDiskProvider diskProvider,
            Logger logger)
        {
            _lyricFileService = lyricFileService;
            _metadataFileService = metadataFileService;
            _otherExtraFileService = otherExtraFileService;
            _diskProvider = diskProvider;
            _logger = logger;
        }

        [EventHandleOrder(EventHandleOrder.First)]
        public void Handle(TrackFileDeletedEvent message)
        {
            PruneStalePendingEntries();

            TrackFile trackFile = message.TrackFile;
            if (message.Reason != DeleteMediaFileReason.ManualOverride ||
                trackFile == null || trackFile.Id <= 0 || string.IsNullOrEmpty(trackFile.Path))
                return;

            try
            {
                List<ExtraFile> rows = Detach(_lyricFileService, trackFile.Id, lyricsOnly: false)
                    .Concat(Detach(_metadataFileService, trackFile.Id, lyricsOnly: true))
                    .Concat(Detach(_otherExtraFileService, trackFile.Id, lyricsOnly: true))
                    .ToList();

                if (rows.Count == 0)
                    return;

                string? artistPath = trackFile.Artist?.Value?.Path;

                List<PendingLyric> entries = rows
                    .Where(x => !string.IsNullOrEmpty(x.RelativePath))
                    .DistinctBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .Select(x => new PendingLyric(
                        x.ArtistId,
                        x.RelativePath,
                        !string.IsNullOrEmpty(x.Extension) ? x.Extension : Path.GetExtension(x.RelativePath),
                        artistPath != null ? Path.Combine(artistPath, x.RelativePath) : null))
                    .ToList();

                if (entries.Count > 0)
                {
                    _pendingRelinks[trackFile.Path] = new PendingLyrics(entries, DateTime.UtcNow);
                    _logger.Debug($"Protected {entries.Count} lyric file(s) from deletion during in-place re-import of: {trackFile.Path}");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to protect lyric files for track file: {trackFile.Path}");
            }
        }

        public void Handle(TrackFileAddedEvent message)
        {
            TrackFile trackFile = message.TrackFile;
            if (trackFile == null || string.IsNullOrEmpty(trackFile.Path))
                return;

            if (!_pendingRelinks.TryRemove(trackFile.Path, out PendingLyrics? pending) || pending == null)
                return;

            foreach (PendingLyric entry in pending.Entries)
            {
                try
                {
                    if (entry.AbsolutePath != null && !_diskProvider.FileExists(entry.AbsolutePath))
                        continue;

                    _lyricFileService.Upsert(new LyricFile
                    {
                        ArtistId = entry.ArtistId,
                        TrackFileId = trackFile.Id,
                        AlbumId = trackFile.AlbumId,
                        RelativePath = entry.RelativePath,
                        Added = DateTime.UtcNow,
                        LastUpdated = DateTime.UtcNow,
                        Extension = entry.Extension
                    });

                    _logger.Debug($"Re-linked protected lyric file to new track file [{trackFile.Id}]: {entry.RelativePath}");
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, $"Failed to re-link protected lyric file for track file: {trackFile.Path}");
                }
            }
        }

        private static List<ExtraFile> Detach<T>(IExtraFileService<T> service, int trackFileId, bool lyricsOnly)
            where T : ExtraFile, new()
        {
            List<T> rows = service.GetFilesByTrackFile(trackFileId);
            if (lyricsOnly)
                rows = rows.Where(LyricsHelper.IsLyricFile).ToList();

            if (rows.Count > 0)
                service.DeleteMany(rows.Select(x => x.Id));

            return rows.Cast<ExtraFile>().ToList();
        }

        private void PruneStalePendingEntries()
        {
            DateTime cutoff = DateTime.UtcNow - PendingLifetime;
            foreach (KeyValuePair<string, PendingLyrics> entry in _pendingRelinks)
            {
                if (entry.Value.CreatedUtc < cutoff)
                    _pendingRelinks.TryRemove(entry.Key, out _);
            }
        }

        private sealed record PendingLyrics(List<PendingLyric> Entries, DateTime CreatedUtc);

        private sealed record PendingLyric(int ArtistId, string RelativePath, string Extension, string? AbsolutePath);
    }
}
