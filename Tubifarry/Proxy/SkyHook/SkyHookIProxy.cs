using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.SkyHook;
using NzbDrone.Core.Music;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.ThingiProvider;
using Tubifarry.Metadata.Consumer;

namespace Tubifarry.Proxy.SkyHook
{
    public class SkyHookIProxy : SkyHookProxy, IProxy
    {
        public SkyHookIProxy(IHttpClient httpClient, IMetadataRequestBuilder requestBuilder, IArtistService artistService, IAlbumService albumService, Logger logger, IMetadataProfileService metadataProfileService, ICacheManager cacheManager) : base(httpClient, requestBuilder, artistService, albumService, logger, metadataProfileService, cacheManager)
        { }

        public Type ConfigContract => typeof(SkyHookConsumerSettings);

        public virtual ProviderMessage? Message => null;

        public IEnumerable<ProviderDefinition> DefaultDefinitions => new List<ProviderDefinition>();

        public ProviderDefinition? Definition { get; set; }

        public object RequestAction(string stage, IDictionary<string, string> query)
        {
            return default;
        }

        protected SkyHookConsumerSettings Settings => (SkyHookConsumerSettings)Definition.Settings;

        public string Name => "Lidarr Default";

        public override string ToString() => GetType().Name;

        public ValidationResult Test()
        {
            throw new NotImplementedException();
        }
    }
}
