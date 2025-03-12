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
using System.Text.RegularExpressions;
using Tubifarry.Metadata.Proxy.Core;
using Tubifarry.Metadata.Proxy.Mixed;

namespace Tubifarry.Metadata.Proxy.SkyHook
{
    public class SkyHookMetadataProxy : SkyHookProxy, IProxy, IMetadata, IProxyProvideArtistInfo, IProxySearchForNewArtist, IProxyProvideAlbumInfo, IProxySearchForNewAlbum, IProxySearchForNewEntity, ISupportMetadataMixing
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

        public ProxyMode ProxyMode { get; set; }

        public override string ToString() => GetType().Name;

        public ValidationResult Test()
        {
            return new();
        }

        public string GetFilenameAfterMove(Artist artist, TrackFile trackFile, MetadataFile metadataFile)
        {
            string existingFilename = Path.Combine(artist.Path, metadataFile.RelativePath);
            string extension = Path.GetExtension(existingFilename).TrimStart('.');
            return Path.ChangeExtension(trackFile.Path, extension);
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

        public MetadataSupportLevel CanHandleSearch(string? albumTitle, string? artistName)
        {
            return MetadataSupportLevel.Supported;
        }
        public MetadataSupportLevel CanHandleIRecordingIds(params string[] recordingIds)
        {
            return MetadataSupportLevel.Supported;
        }

        public MetadataSupportLevel CanHandleChanged()
        {
            return MetadataSupportLevel.Supported;
        }

        /// <summary>
        /// Examines the provided list of links and returns the MusicBrainz GUID if one is found.
        /// Recognizes URLs such as:
        ///   https://musicbrainz.org/artist/xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
        ///   https://musicbrainz.org/release/xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
        ///   https://musicbrainz.org/recording/xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
        /// </summary>
        public string? SupportsLink(List<Links> links)
        {
            if (links == null || links.Count == 0)
                return null;
            Regex musicBrainzRegex = new(
                @"musicbrainz\.org\/(?:artist|release|recording)\/([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})",
                RegexOptions.IgnoreCase);

            foreach (Links link in links)
            {
                if (string.IsNullOrWhiteSpace(link.Url))
                    continue;

                Match match = musicBrainzRegex.Match(link.Url);
                if (match.Success && match.Groups.Count > 1)
                    return match.Groups[1].Value;
            }
            return null;
        }

        /// <summary>
        /// Checks if the given id is in MusicBrainz GUID format (and does not contain an '@').
        /// Returns Supported if valid; otherwise, Unsupported.
        /// </summary>
        public MetadataSupportLevel CanHandleId(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Contains('@'))
                return MetadataSupportLevel.Unsupported;

            Regex guidRegex = new(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.IgnoreCase);

            if (guidRegex.IsMatch(id))
                return MetadataSupportLevel.Supported;

            return MetadataSupportLevel.Unsupported;
        }
    }
}
