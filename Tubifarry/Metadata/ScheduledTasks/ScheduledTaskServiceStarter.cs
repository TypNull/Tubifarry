using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;

namespace Tubifarry.Metadata.ScheduledTasks
{
    public class ScheduledTaskServiceStarter : IHandle<ApplicationStartedEvent>
    {
        public static IScheduledTaskService? TaskService { get; private set; }

        public ScheduledTaskServiceStarter(IScheduledTaskService scheduledTaskService) => TaskService = scheduledTaskService;

        public void Handle(ApplicationStartedEvent message) => TaskService?.InitializeTasks();
    }
}