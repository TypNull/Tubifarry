using FluentValidation.Results;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using NzbDrone.Core.ThingiProvider;

namespace Tubifarry.Metadata.Proxy
{
    public abstract class ProxyBase<TSettings> : IProxy, IMetadata where TSettings : IProviderConfig, new()
    {
        public abstract string Name { get; }
        public Type ConfigContract => typeof(TSettings);
        public virtual ProviderMessage? Message => null;
        public IEnumerable<ProviderDefinition> DefaultDefinitions => new List<ProviderDefinition>();
        public ProviderDefinition? Definition { get; set; }

        protected TSettings? Settings => Definition?.Settings == null ? default : (TSettings)Definition!.Settings;

        public virtual object RequestAction(string action, IDictionary<string, string> query) => default!;
        public override string ToString() => GetType().Name;

        // IMetadata implementation
        public virtual string GetFilenameAfterMove(Artist artist, TrackFile trackFile, MetadataFile metadataFile) =>
            Path.ChangeExtension(trackFile.Path, Path.GetExtension(Path.Combine(artist.Path, metadataFile.RelativePath)).TrimStart('.'));

        public virtual string GetFilenameAfterMove(Artist artist, string albumPath, MetadataFile metadataFile) =>
            Path.Combine(artist.Path, albumPath, Path.GetFileName(metadataFile.RelativePath));

        public virtual MetadataFile FindMetadataFile(Artist artist, string path) => default!;
        public virtual MetadataFileResult ArtistMetadata(Artist artist) => default!;
        public virtual MetadataFileResult AlbumMetadata(Artist artist, Album album, string albumPath) => default!;
        public virtual MetadataFileResult TrackMetadata(Artist artist, TrackFile trackFile) => default!;
        public virtual List<ImageFileResult> ArtistImages(Artist artist) => new();
        public virtual List<ImageFileResult> AlbumImages(Artist artist, Album album, string albumPath) => new();
        public virtual List<ImageFileResult> TrackImages(Artist artist, TrackFile trackFile) => new();

        public virtual ValidationResult Test() => new();
    }
}