using FluentValidation.Results;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using NzbDrone.Core.ThingiProvider;

namespace Tubifarry.Metadata.Proxy.Core
{
    public enum ProxyMode { Public, Internal }
    public interface IProxy : IProvider, IProxyProvideArtistInfo, IProxySearchForNewArtist, IProxyProvideAlbumInfo, IProxySearchForNewAlbum, IProxySearchForNewEntity
    {
        public ProxyMode ProxyMode { get; protected set; }
    }
    public abstract class ConsumerProxyPlaceholder<TSettings> : IMetadata, IProxy
      where TSettings : IProviderConfig, new()
    {
        protected Lazy<IProxyService> _proxyService;

        public ProxyMode ProxyMode { get; set; }

        protected ConsumerProxyPlaceholder(Lazy<IProxyService> proxyService) => _proxyService = proxyService;

        public abstract string Name { get; }

        public Type ConfigContract => typeof(TSettings);

        public virtual ProviderMessage? Message => null;

        public IEnumerable<ProviderDefinition> DefaultDefinitions => new List<ProviderDefinition>();

        public ProviderDefinition? Definition { get; set; }

        public string GetFilenameAfterMove(Artist artist, TrackFile trackFile, MetadataFile metadataFile) => Path.ChangeExtension(trackFile.Path, Path.GetExtension(Path.Combine(artist.Path, metadataFile.RelativePath)).TrimStart('.'));
        public string GetFilenameAfterMove(Artist artist, string albumPath, MetadataFile metadataFile) => Path.Combine(artist.Path, albumPath, Path.GetFileName(metadataFile.RelativePath));
        public MetadataFile FindMetadataFile(Artist artist, string path) => default!;
        public MetadataFileResult ArtistMetadata(Artist artist) => default!;
        public MetadataFileResult AlbumMetadata(Artist artist, Album album, string albumPath) => default!;
        public MetadataFileResult TrackMetadata(Artist artist, TrackFile trackFile) => default!;
        public List<ImageFileResult> ArtistImages(Artist artist) => new();
        public List<ImageFileResult> AlbumImages(Artist artist, Album album, string albumPath) => new();
        public List<ImageFileResult> TrackImages(Artist artist, TrackFile trackFile) => new();
        public virtual object RequestAction(string action, IDictionary<string, string> query) => default!;

        protected TSettings? Settings => Definition?.Settings == null ? default : (TSettings)Definition!.Settings;

        public override string ToString() => GetType().Name;

        public abstract Tuple<string, Album, List<ArtistMetadata>> GetAlbumInfo(string id);

        public abstract Artist GetArtistInfo(string lidarrId, int metadataProfileId);

        public abstract HashSet<string> GetChangedAlbums(DateTime startTime);

        public abstract HashSet<string> GetChangedArtists(DateTime startTime);

        public abstract List<Album> SearchForNewAlbum(string title, string artist);

        public abstract List<Album> SearchForNewAlbumByRecordingIds(List<string> recordingIds);

        public abstract List<Artist> SearchForNewArtist(string title);

        public abstract List<object> SearchForNewEntity(string title);

        public abstract ValidationResult Test();
    }
}
