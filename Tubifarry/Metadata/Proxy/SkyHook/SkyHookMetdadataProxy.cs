using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.SkyHook;
using NzbDrone.Core.Music;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.ThingiProvider;

namespace Tubifarry.Metadata.Proxy.SkyHook
{
    public class SkyHookMetadataProxy : SkyHookProxy, IProxy, IMetadata
    {
        public SkyHookMetadataProxy(IHttpClient httpClient, IMetadataRequestBuilder requestBuilder, IArtistService artistService, IAlbumService albumService, Logger logger, IMetadataProfileService metadataProfileService, ICacheManager cacheManager) : base(httpClient, requestBuilder, artistService, albumService, logger, metadataProfileService, cacheManager)
        { }

        public Type ConfigContract => typeof(SykHookMetadataProxySettings);

        public virtual ProviderMessage? Message => null;

        public IEnumerable<ProviderDefinition> DefaultDefinitions => new List<ProviderDefinition>();

        public ProviderDefinition? Definition { get; set; }

        public object RequestAction(string stage, IDictionary<string, string> query) => default!;

        protected SykHookMetadataProxySettings Settings => (SykHookMetadataProxySettings)Definition!.Settings;

        public string Name => "Lidarr Default";

        public override string ToString() => GetType().Name;

        public ValidationResult Test()
        {
            return new();
        }

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
    }
}
