using NzbDrone.Core.Datastore;
using NzbDrone.Core.Music;

namespace Tubifarry.Metadata.Proxy.DiscogsProxy
{
    internal class LazyAlbumLoader : LazyLoaded<List<Album>>
    {
        private readonly string _artistName;
        private readonly DiscogsProxy _proxy;
        private readonly DiscogsMetadataProxySettings _settings;

        public LazyAlbumLoader(string artistName, DiscogsProxy proxy, DiscogsMetadataProxySettings settings)
        {
            _artistName = artistName;
            _proxy = proxy;
            _settings = settings;
        }

        public override void LazyLoad()
        {
            if (!IsLoaded)
            {
                _value = _proxy.FetchAlbumsForArtistAsync(_settings, _artistName)
                    .GetAwaiter().GetResult()
                    .Where(a => a != null)
                    .ToList();
                IsLoaded = true;
            }
        }
    }
}