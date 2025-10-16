using NLog;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Plugins;
#if CI
using NzbDrone.Core.Plugins.Commands;
#endif
using NzbDrone.Core.Profiles.Delay;
using Tubifarry.Core.Utilities;

namespace Tubifarry
{
    public class Tubifarry : Plugin
#if! MASTER_BRANCH
        , IHandle<ApplicationStartingEvent>
#endif
    {
        private readonly Logger _logger;
        private readonly Lazy<IPluginService> _pluginService;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IPluginSettings _pluginSettings;

        public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36"; //$"{PluginInfo.Name}/{PluginInfo.AssemblyVersion} ({PluginInfo.Framework} {PluginInfo.Branch})";

        public override string Name => PluginInfo.Name;
        public override string Owner => PluginInfo.Author;
        public override string GithubUrl => PluginInfo.RepoUrl;

        private static Type[] ProtocolTypes => new Type[] { typeof(YoutubeDownloadProtocol), typeof(SoulseekDownloadProtocol), typeof(LucidaDownloadProtocol), typeof(QobuzDownloadProtocol) };
        public static TimeSpan AverageRuntime { get; private set; } = TimeSpan.FromDays(4);
        public static DateTime LastStarted { get; private set; } = DateTime.UtcNow;

        public Tubifarry(IDelayProfileRepository repo, IPluginSettings pluginSettings, IEnumerable<IDownloadProtocol> downloadProtocols, Lazy<IPluginService> pluginService, IManageCommandQueue commandQueueManager, Logger logger)
        {
            _logger = logger;
            _commandQueueManager = commandQueueManager;
            _pluginService = pluginService;
            _pluginSettings = pluginSettings;
            CheckDelayProfiles(repo, downloadProtocols);
        }


        private void CheckDelayProfiles(IDelayProfileRepository repo, IEnumerable<IDownloadProtocol> downloadProtocols)
        {
            foreach (IDownloadProtocol protocol in downloadProtocols.Where(x => ProtocolTypes.Any(y => y == x.GetType())))
            {
                _logger.Trace($"Checking protocol: {protocol.GetType().Name}");

                foreach (DelayProfile? profile in repo.All())
                {
                    if (!profile.Items.Any(x => x.Protocol == protocol.GetType().Name))
                    {
                        _logger.Debug($"Added protocol to DelayProfile (ID: {profile.Id})");
                        profile.Items.Add(GetProtocolItem(protocol, true));
                        repo.Update(profile);
                    }
                }
            }
        }

        private static DelayProfileProtocolItem GetProtocolItem(IDownloadProtocol protocol, bool allowed) => new()
        {
            Name = protocol.GetType().Name.Replace("DownloadProtocol", ""),
            Protocol = protocol.GetType().Name,
            Allowed = allowed
        };

        public void Handle(ApplicationStartingEvent message)
        {
#if CI
            AvailableVersion = _pluginService.Value.GetRemotePlugin(GithubUrl).Version;
            if (AvailableVersion > InstalledVersion)
                _commandQueueManager.Push(new InstallPluginCommand() { GithubUrl = GithubUrl });
#endif
            List<DateTime> lastStarted = _pluginSettings.GetValue<List<DateTime>>("lastStarted") ?? [];

            LastStarted = DateTime.UtcNow;
            lastStarted.Add(LastStarted);
            if (lastStarted.Count > 10)
                lastStarted.RemoveAt(0);
            _pluginSettings.SetValue("lastStarted", lastStarted);

            if (lastStarted.Count > 1)
            {
                lastStarted.Sort();
                TimeSpan totalRuntime = TimeSpan.Zero;
                for (int i = 1; i < lastStarted.Count; i++)
                {
                    TimeSpan timeBetweenStarts = lastStarted[i] - lastStarted[i - 1];
                    if (timeBetweenStarts < TimeSpan.FromDays(30))
                        totalRuntime += timeBetweenStarts;
                }
                int validIntervals = Math.Max(1, lastStarted.Count - 1);
                AverageRuntime = TimeSpan.FromTicks(totalRuntime.Ticks / validIntervals);

                _logger.Debug($"Average runtime between restarts is {AverageRuntime.TotalDays:F2} days");
            }
        }
    }
}
