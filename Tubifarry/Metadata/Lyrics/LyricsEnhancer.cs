using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Music;
using Tubifarry.Metadata.ScheduledTasks;

namespace Tubifarry.Metadata.Lyrics
{
    public class LyricsEnhancer : ScheduledTaskBase<LyricsEnhancerSettings>, IExecute<LyricsUpdateCommand>
    {
        private readonly ILyricsEnhancerService _service;

        public LyricsEnhancer(ILyricsEnhancerService service) => _service = service;

        public override string Name => "Lyrics Enhancer";
        public override Type CommandType => typeof(LyricsUpdateCommand);
        public override CommandPriority Priority => CommandPriority.Low;

        public override int IntervalMinutes => ActiveSettings.EnableScheduledUpdates
            ? (int)TimeSpan.FromDays(ActiveSettings.UpdateInterval).TotalMinutes
            : 0;

        private LyricsEnhancerSettings ActiveSettings => Settings ?? LyricsEnhancerSettings.Instance!;

        public void Execute(LyricsUpdateCommand message) => _service.Execute(message, ActiveSettings);

        public override MetadataFileResult TrackMetadata(Artist artist, TrackFile trackFile) =>
            _service.TrackMetadata(artist, trackFile, ActiveSettings);

        public override string GetFilenameAfterMove(Artist artist, TrackFile trackFile, MetadataFile metadataFile) =>
            _service.GetFilenameAfterMove(artist, trackFile, metadataFile);
    }
}
