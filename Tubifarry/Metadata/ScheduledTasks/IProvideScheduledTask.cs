using NzbDrone.Core.Messaging.Commands;

namespace Tubifarry.Metadata.ScheduledTasks
{
    /// <summary>
    /// Interface for providers that can register scheduled tasks.
    /// Metadata providers implementing this interface will automatically
    /// have their tasks registered when enabled.
    /// </summary>
    public interface IProvideScheduledTask
    {
        /// <summary>
        /// The command type that will be executed on schedule.
        /// </summary>
        Type CommandType { get; }

        /// <summary>
        /// The interval in minutes between task executions.
        /// </summary>
        int IntervalMinutes { get; }

        /// <summary>
        /// The priority of the command execution.
        /// </summary>
        CommandPriority Priority { get; }
    }
}