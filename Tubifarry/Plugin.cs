using NLog;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Profiles.Delay;

namespace NzbDrone.Core.Plugins
{
    public class Tubifarry : Plugin
    {
        public override string Name => "Tubifarry";
        public override string Owner => "TypNull";
        public override string GithubUrl => "https://github.com/TypNull/Tubifarry";

        private static Type[] ProtocolTypes => new Type[] { typeof(YoutubeDownloadProtocol), typeof(SoulseekDownloadProtocol) };

        public Tubifarry(IDelayProfileRepository repo, IEnumerable<IDownloadProtocol> downloadProtocols, Logger logger) => CheckDelayProfiles(repo, downloadProtocols, logger);

        private static void CheckDelayProfiles(IDelayProfileRepository repo, IEnumerable<IDownloadProtocol> downloadProtocols, Logger logger)
        {
            IEnumerable<IDownloadProtocol> protocols = downloadProtocols.Where(x => ProtocolTypes.Any(y => y == x.GetType()));

            foreach (IDownloadProtocol protocol in protocols)
            {
                logger.Trace($"Checking Protokol: {protocol.GetType().Name}");

                foreach (DelayProfile? profile in repo.All())
                {
                    if (!profile.Items.Any(x => x.Protocol == protocol.GetType().Name))
                    {
                        logger.Debug($"Added protocol to DelayProfile (ID: {profile.Id})");
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
    }
}
