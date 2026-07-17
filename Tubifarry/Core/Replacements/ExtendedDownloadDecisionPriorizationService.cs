using NzbDrone.Core.Configuration;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Profiles.Delay;
using NzbDrone.Core.Qualities;

namespace Tubifarry.Core.Replacements;

public class ExtendedDownloadDecisionPriorizationService : IPrioritizeDownloadDecision
{
    private readonly ExtendedDownloadDecisionComparer _comparer;

    public ExtendedDownloadDecisionPriorizationService(
        IConfigService configService,
        IDelayProfileService delayProfileService,
        IQualityDefinitionService qualityDefinitionService)
    {
        _comparer = new ExtendedDownloadDecisionComparer(configService, delayProfileService, qualityDefinitionService);
    }

    public List<DownloadDecision> PrioritizeDecisions(List<DownloadDecision> decisions)
    {
        return decisions.Where(c => c.RemoteAlbum.DownloadAllowed)
                        .GroupBy(c => c.RemoteAlbum.Artist.Id, (artistId, downloadDecisions) => downloadDecisions.OrderByDescending(decision => decision, _comparer))
                        .SelectMany(c => c)
                        .Union(decisions.Where(c => !c.RemoteAlbum.DownloadAllowed))
                        .ToList();
    }
}
