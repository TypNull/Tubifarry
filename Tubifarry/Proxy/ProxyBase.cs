using FluentValidation.Results;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Music;
using NzbDrone.Core.ThingiProvider;

namespace Tubifarry.Proxy
{
    public interface IProxy : IProvider, IProvideArtistInfo, ISearchForNewArtist, IProvideAlbumInfo, ISearchForNewAlbum, ISearchForNewEntity { }

    public abstract class ProxyBase<TMetadata, TSettings> : IProxy where TMetadata : MetadataBase<TSettings> where TSettings : IProviderConfig, new()
    {
        public abstract string Name { get; }

        protected TSettings Settings => (TSettings)Definition!.Settings;

        public Type ConfigContract => typeof(TSettings);

        public virtual ProviderMessage? Message => null;

        public IEnumerable<ProviderDefinition> DefaultDefinitions => new List<ProviderDefinition>();

        public ProviderDefinition? Definition { get; set; }

        public abstract Tuple<string, Album, List<ArtistMetadata>> GetAlbumInfo(string id);

        public abstract Artist GetArtistInfo(string lidarrId, int metadataProfileId);

        public abstract HashSet<string> GetChangedAlbums(DateTime startTime);

        public abstract HashSet<string> GetChangedArtists(DateTime startTime);

        public abstract List<Album> SearchForNewAlbum(string title, string artist);

        public abstract List<Album> SearchForNewAlbumByRecordingIds(List<string> recordingIds);

        public abstract List<Artist> SearchForNewArtist(string title);

        public abstract List<object> SearchForNewEntity(string title);

        public abstract ValidationResult Test();

        public object RequestAction(string stage, IDictionary<string, string> query)
        {
            return default!;
        }

        public override string ToString() => GetType().Name;
    }
}
