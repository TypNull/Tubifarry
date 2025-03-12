using NLog;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Plugins;
using NzbDrone.Core.Plugins.Commands;
using NzbDrone.Core.Profiles.Delay;

namespace Tubifarry
{
    public class Tubifarry : Plugin
#if MASTER_BRANCH
        , IHandle<ApplicationStartingEvent>
#endif
    {
        private readonly Logger _logger;
        private readonly IPluginService _pluginService;
        private readonly IManageCommandQueue _commandQueueManager;

        public override string Name => PluginInfo.Name;
        public override string Owner => PluginInfo.Author;
        public override string GithubUrl => $"https://github.com/{PluginInfo.Name}/{PluginInfo.Author}/branch/{PluginInfo.Branch}";

        private static Type[] ProtocolTypes => new Type[] { typeof(YoutubeDownloadProtocol), typeof(SoulseekDownloadProtocol) };

        public Tubifarry(IDelayProfileRepository repo, IEnumerable<IDownloadProtocol> downloadProtocols, IPluginService pluginService, IManageCommandQueue commandQueueManager, Logger logger)
        {
            _logger = logger;
            _commandQueueManager = commandQueueManager;
            _pluginService = pluginService;
            CheckDelayProfiles(repo, downloadProtocols);
        }


        private void CheckDelayProfiles(IDelayProfileRepository repo, IEnumerable<IDownloadProtocol> downloadProtocols)
        {
            foreach (IDownloadProtocol protocol in downloadProtocols.Where(x => ProtocolTypes.Any(y => y == x.GetType())))
            {
                _logger.Trace($"Checking Protokol: {protocol.GetType().Name}");

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
            AvailableVersion = _pluginService.GetRemotePlugin(GithubUrl).Version;
            if (AvailableVersion > InstalledVersion)
                _commandQueueManager.Push(new InstallPluginCommand() { GithubUrl = GithubUrl });
        }
    }
}
