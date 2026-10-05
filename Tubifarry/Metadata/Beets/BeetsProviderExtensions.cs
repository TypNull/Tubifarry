using NzbDrone.Core.Extras.Metadata;

namespace Tubifarry.Metadata.Beets
{
    public static class BeetsProviderExtensions
    {
        public static BeetsSettings? GetEnabledBeetsSettings(this IMetadataFactory metadataFactory) =>
            metadataFactory.Enabled()
                .Select(consumer => consumer.Definition)
                .Where(definition => string.Equals(definition.Implementation, nameof(BeetsMetadata), StringComparison.Ordinal))
                .Select(definition => definition.Settings as BeetsSettings)
                .FirstOrDefault(settings => settings != null);
    }
}
