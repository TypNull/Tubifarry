using FluentValidation.Results;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Music;
using NzbDrone.Core.ThingiProvider;

namespace Tubifarry.Metadata.ScheduledTasks
{
    /// <summary>
    /// Base class for metadata providers that want to register scheduled tasks.
    /// Provides automatic task registration/unregistration when enabled/disabled.
    /// </summary>
    /// <typeparam name="TSettings">The settings type for this scheduled task provider.</typeparam>
    public abstract class ScheduledTaskBase<TSettings> : IProvideScheduledTask, IMetadata
        where TSettings : IProviderConfig, new()
    {
        public abstract string Name { get; }

        public Type ConfigContract => typeof(TSettings);

        public virtual ProviderMessage? Message => null;

        public IEnumerable<ProviderDefinition> DefaultDefinitions => [];

        public ProviderDefinition? Definition { get; set; }

        protected virtual TSettings? Settings => Definition?.Settings == null ? default : (TSettings)Definition.Settings;

        public abstract Type CommandType { get; }

        public abstract int IntervalMinutes { get; }

        public virtual CommandPriority Priority => CommandPriority.Low;
        public virtual object RequestAction(string action, IDictionary<string, string> query) => default!;

        public virtual ValidationResult Test() => new();

        public virtual string GetFilenameAfterMove(Artist artist, TrackFile trackFile, MetadataFile metadataFile) =>
            Path.ChangeExtension(trackFile.Path, Path.GetExtension(Path.Combine(artist.Path, metadataFile.RelativePath)).TrimStart('.'));

        public virtual string GetFilenameAfterMove(Artist artist, string albumPath, MetadataFile metadataFile) =>
            Path.Combine(artist.Path, albumPath, Path.GetFileName(metadataFile.RelativePath));
        public virtual MetadataFile FindMetadataFile(Artist artist, string path) => default!;

        public virtual MetadataFileResult ArtistMetadata(Artist artist) => default!;

        public virtual MetadataFileResult AlbumMetadata(Artist artist, Album album, string albumPath) => default!;

        public virtual MetadataFileResult TrackMetadata(Artist artist, TrackFile trackFile) => default!;

        public virtual List<ImageFileResult> ArtistImages(Artist artist) => [];

        public virtual List<ImageFileResult> AlbumImages(Artist artist, Album album, string albumPath) => [];

        public virtual List<ImageFileResult> TrackImages(Artist artist, TrackFile trackFile) => [];

        public override string ToString() => Name;
    }
}