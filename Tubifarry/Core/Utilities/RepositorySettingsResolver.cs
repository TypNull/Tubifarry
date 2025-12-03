using Microsoft.Extensions.DependencyInjection;
using NLog;
using NzbDrone.Core.ThingiProvider;

namespace Tubifarry.Core.Utilities
{
    public interface IRepositorySettingsResolver
    {
        TSettings Resolve<TRepository, TSettings>(string implementationName)
            where TRepository : class
            where TSettings : IProviderConfig;
    }

    public class RepositorySettingsResolver(Lazy<IServiceProvider> serviceProvider, Logger logger) : IRepositorySettingsResolver
    {
        private readonly Lazy<IServiceProvider> _serviceProvider = serviceProvider;
        private readonly Logger _logger = logger;

        public TSettings Resolve<TRepository, TSettings>(string implementationName)
            where TRepository : class
            where TSettings : IProviderConfig
        {
            dynamic dynamicRepo = _serviceProvider.Value.GetRequiredService<TRepository>();
            IEnumerable<ProviderDefinition> definitions = dynamicRepo.All();

            ProviderDefinition? definition = definitions.FirstOrDefault(d => d.Implementation == implementationName);

            if (definition?.Settings is TSettings settings)
            {
                _logger.Debug($"Resolved settings for {implementationName} from {typeof(TRepository).Name}");
                return settings;
            }

            throw new InvalidOperationException($"Settings not found for {implementationName}. Ensure it's configured and enabled.");
        }
    }
}
