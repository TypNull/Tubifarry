using FluentValidation.Results;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using NzbDrone.Core.ThingiProvider;

namespace Tubifarry.Proxy
{
    public abstract class ConsumerProxyPlaceholder<TSettings> : IMetadata
        where TSettings : IProviderConfig, new()
    {
        public abstract string Name { get; }

        public Type ConfigContract => typeof(TSettings);

        public virtual ProviderMessage? Message => null;

        public IEnumerable<ProviderDefinition> DefaultDefinitions => new List<ProviderDefinition>();

        public ProviderDefinition? Definition { get; set; }

        public string GetFilenameAfterMove(Artist artist, TrackFile trackFile, MetadataFile metadataFile)
        {
            string existingFilename = Path.Combine(artist.Path, metadataFile.RelativePath);
            string extension = Path.GetExtension(existingFilename).TrimStart('.');
            string newFileName = Path.ChangeExtension(trackFile.Path, extension);

            return newFileName;
        }

        public string GetFilenameAfterMove(Artist artist, string albumPath, MetadataFile metadataFile)
        {
            string existingFilename = Path.GetFileName(metadataFile.RelativePath);
            string newFileName = Path.Combine(artist.Path, albumPath, existingFilename);

            return newFileName;
        }

        public MetadataFile FindMetadataFile(Artist artist, string path) => default!;
        public MetadataFileResult ArtistMetadata(Artist artist) => default!;
        public MetadataFileResult AlbumMetadata(Artist artist, Album album, string albumPath) => default!;
        public MetadataFileResult TrackMetadata(Artist artist, TrackFile trackFile) => default!;
        public List<ImageFileResult> ArtistImages(Artist artist) => new();
        public List<ImageFileResult> AlbumImages(Artist artist, Album album, string albumPath) => new();
        public List<ImageFileResult> TrackImages(Artist artist, TrackFile trackFile) => new();

        public abstract ValidationResult Test();
        public virtual object RequestAction(string action, IDictionary<string, string> query) => default!;

        protected TSettings? Settings => (TSettings)Definition!.Settings;

        public override string ToString() => GetType().Name;
    }
}
