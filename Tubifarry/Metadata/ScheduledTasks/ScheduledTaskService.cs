using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Jobs;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.ThingiProvider.Events;

namespace Tubifarry.Metadata.ScheduledTasks
{
    public interface IScheduledTaskService
    {
        IEnumerable<IProvideScheduledTask> TaskProviders { get; }
        IEnumerable<IProvideScheduledTask> ActiveTaskProviders { get; }
        void InitializeTasks();
        void UpdateTask<T>() where T : IProvideScheduledTask;
        void EnableTask(IProvideScheduledTask provider);
        void DisableTask(IProvideScheduledTask provider);
    }

    public enum TaskStatusAction
    {
        Enabled,
        Disabled,
        IntervalUpdated
    }

    public class ScheduledTaskService(
        IScheduledTaskRepository _scheduledTaskRepository,
        IMetadataFactory _metadataFactory,
        ICacheManager _cacheManager,
        Logger _logger) : IScheduledTaskService, IHandle<ProviderUpdatedEvent<IMetadata>>, IHandle<ProviderAddedEvent<IMetadata>>, IHandle<ProviderDeletedEvent<IMetadata>>
    {
        private readonly Dictionary<string, IProvideScheduledTask> _registeredTasks = [];
        private readonly List<IProvideScheduledTask> _activeTaskProviders = [];

        public IEnumerable<IProvideScheduledTask> TaskProviders { get; private set; } = [];
        public IEnumerable<IProvideScheduledTask> ActiveTaskProviders => _activeTaskProviders;

        public void InitializeTasks()
        {
            _logger.Trace("Initializing scheduled task system");

            IEnumerable<IProvideScheduledTask> taskProviders = _metadataFactory.GetAvailableProviders()
                .OfType<IProvideScheduledTask>()
                .Where(ValidateTaskProvider)
                .DistinctBy(x => x.CommandType.FullName)
                .ToArray();

            TaskProviders = taskProviders;

            foreach (IProvideScheduledTask provider in taskProviders.Where(x => (x as IProvider)?.Definition?.Enable == true))
                EnableTask(provider);
            _logger.Debug($"Initialized scheduled task system: {_activeTaskProviders.Count} active tasks, {TaskProviders.Count()} total task providers");
        }

        public void Handle(ProviderUpdatedEvent<IMetadata> message)
        {
            if (TaskProviders.FirstOrDefault(x =>
            (x as IMetadata)?.Definition?.ImplementationName == message.Definition.ImplementationName)
                is not IProvideScheduledTask taskProvider)
            {
                return;
            }

            _logger.Trace($"Provider updated event for: {(taskProvider as IProvider)?.Name}, Enabled: {message.Definition.Enable}");

            if (message.Definition.Enable)
                EnableTask(taskProvider);
            else
                DisableTask(taskProvider);

        }

        public void Handle(ProviderAddedEvent<IMetadata> message)
        {
            if (message.Definition.Implementation == null)
                return;

            IProvideScheduledTask? taskProvider = TaskProviders.FirstOrDefault(x =>
                (x as IProvider)?.Definition?.ImplementationName == message.Definition.ImplementationName);

            if (taskProvider != null && message.Definition.Enable)
                EnableTask(taskProvider);
        }

        public void Handle(ProviderDeletedEvent<IMetadata> message)
        {
            IProvideScheduledTask? taskProvider = _activeTaskProviders.FirstOrDefault(x =>
                (x as IProvider)?.Definition?.Id == message.ProviderId);

            if (taskProvider != null)
                DisableTask(taskProvider);
        }

        public void EnableTask(IProvideScheduledTask provider)
        {
            if (provider.IntervalMinutes <= 0)
            {
                _logger.Trace($"Task has interval <= 0, treating as disabled: {(provider as IProvider)?.Name}");
                DisableTask(provider);
                return;
            }

            if (_activeTaskProviders.Contains(provider))
            {
                _logger.Trace($"Task already enabled: {(provider as IProvider)?.Name}");
                UpdateTaskInterval(provider);
                return;
            }

            _activeTaskProviders.Add(provider);
            RegisterTask(provider);
            _logger.Info($"Enabled scheduled task: {(provider as IProvider)?.Name} (Interval: {provider.IntervalMinutes}m, Priority: {provider.Priority})");
        }

        public void DisableTask(IProvideScheduledTask provider)
        {
            if (!_activeTaskProviders.Contains(provider))
            {
                _logger.Debug($"Task already disabled: {(provider as IProvider)?.Name}");
                return;
            }

            string typeName = provider.CommandType.FullName!;

            try
            {
                ScheduledTask? existing = _scheduledTaskRepository.All().SingleOrDefault(t => t.TypeName == typeName);

                if (existing != null)
                {
                    _scheduledTaskRepository.Delete(existing.Id);
                    RemoveFromCache(typeName);
                    _logger.Debug($"Deleted scheduled task from repository: {(provider as IProvider)?.Name}");
                }

                _registeredTasks.Remove(typeName);
                _activeTaskProviders.Remove(provider);
                _logger.Info($"Disabled scheduled task: {(provider as IProvider)?.Name}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to disable scheduled task: {(provider as IProvider)?.Name}");
            }
        }

        private void RegisterTask(IProvideScheduledTask provider)
        {
            string typeName = provider.CommandType.FullName!;

            try
            {
                ScheduledTask task = new()
                {
                    Interval = provider.IntervalMinutes,
                    TypeName = typeName,
                    Priority = provider.Priority
                };

                ScheduledTask? existing = _scheduledTaskRepository.All().SingleOrDefault(t => t.TypeName == typeName);

                if (existing != null)
                {
                    existing.Interval = task.Interval;
                    existing.Priority = task.Priority;
                    _scheduledTaskRepository.Update(existing);
                    _logger.Trace($"Updated existing scheduled task: {(provider as IProvider)?.Name}");
                }
                else
                {
                    task.LastExecution = DateTime.UtcNow;
                    _scheduledTaskRepository.Insert(task);
                    _logger.Trace($"Inserted new scheduled task: {(provider as IProvider)?.Name}");
                }

                UpdateCache(existing ?? task);
                _registeredTasks[typeName] = provider;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to register scheduled task: {(provider as IProvider)?.Name}");
            }
        }

        public void UpdateTask<T>() where T : IProvideScheduledTask
        {
            string typeName = typeof(T).FullName!;

            if (!_registeredTasks.TryGetValue(typeName, out IProvideScheduledTask? provider))
            {
                _logger.Warn($"Cannot update interval: Task provider {typeName} is not registered");
                return;
            }

            if (provider is not T)
            {
                _logger.Warn($"Cannot update interval: Registered provider for {typeName} is not of expected type");
                return;
            }

            EnableTask(provider);
        }

        private void UpdateTaskInterval(IProvideScheduledTask provider)
        {
            string typeName = provider.CommandType.FullName!;
            try
            {
                ScheduledTask? existing = _scheduledTaskRepository.All().SingleOrDefault(t => t.TypeName == typeName);

                if (existing != null && existing.Interval != provider.IntervalMinutes)
                {
                    existing.Interval = provider.IntervalMinutes;
                    _scheduledTaskRepository.Update(existing);
                    UpdateCache(existing);
                    _logger.Trace($"Updated task interval for {(provider as IProvider)?.Name}: {provider.IntervalMinutes}m");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to update task interval for: {(provider as IProvider)?.Name}");
            }
        }

        private void UpdateCache(ScheduledTask task)
        {
            ICached<ScheduledTask> cache = _cacheManager.GetCache<ScheduledTask>(typeof(TaskManager));
            cache.Set(task.TypeName, task);
        }

        private void RemoveFromCache(string typeName)
        {
            ICached<ScheduledTask> cache = _cacheManager.GetCache<ScheduledTask>(typeof(TaskManager));
            cache.Remove(typeName);
        }

        private bool ValidateTaskProvider(IProvideScheduledTask provider)
        {
            try
            {
                if (provider.CommandType == null)
                {
                    _logger.Warn($"Task provider {(provider as IProvider)?.Name} has no command type. Provider will be excluded.");
                    return false;
                }

                if (provider.IntervalMinutes < 0)
                {
                    _logger.Warn($"Task provider {(provider as IProvider)?.Name} has invalid interval ({provider.IntervalMinutes}m). Provider will be excluded.");
                    return false;
                }

                _logger.Trace($"Validated task provider: {(provider as IProvider)?.Name}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, $"Failed to validate task provider {(provider as IProvider)?.Name}. Provider will be excluded.");
                return false;
            }
        }
    }
}