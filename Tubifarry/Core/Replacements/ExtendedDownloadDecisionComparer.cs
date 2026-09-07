using NzbDrone.Core.Configuration;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Delay;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace Tubifarry.Core.Replacements;

public sealed class ExtendedDownloadDecisionComparer(
    IConfigService configService,
    IDelayProfileService delayProfileService,
    IQualityDefinitionService qualityDefinitionService,
    IAlbumYearMatcher yearMatcher) : DownloadDecisionComparer(configService, delayProfileService, qualityDefinitionService, yearMatcher), IComparer<DownloadDecision>
{
    private const int HealthBucketSize = 250;

    public new int Compare(DownloadDecision x, DownloadDecision y)
    {
        int health = CompareShareHealth(x, y);
        if (health == 0)
            return base.Compare(x, y);

        int quality = CompareQuality(x, y);
        if (quality != 0)
            return quality;

        int customFormat = x.RemoteAlbum.CustomFormatScore.CompareTo(y.RemoteAlbum.CustomFormatScore);
        if (customFormat != 0)
            return customFormat;

        int yearMatch = CompareYearMatch(x, y);
        if (yearMatch != 0)
            return yearMatch;

        int protocol = CompareProtocol(x, y);
        if (protocol != 0)
            return protocol;

        int indexerPriority = y.RemoteAlbum.Release.IndexerPriority.CompareTo(x.RemoteAlbum.Release.IndexerPriority);
        if (indexerPriority != 0)
            return indexerPriority;

        return health;
    }

    private static int CompareShareHealth(DownloadDecision x, DownloadDecision y)
    {
        if (x.RemoteAlbum.Release.DownloadProtocol == nameof(TorrentDownloadProtocol) &&
            y.RemoteAlbum.Release.DownloadProtocol == nameof(TorrentDownloadProtocol))
            return 0;

        int? seedersX = TorrentInfo.GetSeeders(x.RemoteAlbum.Release);
        int? seedersY = TorrentInfo.GetSeeders(y.RemoteAlbum.Release);

        if (!seedersX.HasValue || !seedersY.HasValue)
            return 0;

        return (seedersX.Value / HealthBucketSize).CompareTo(seedersY.Value / HealthBucketSize);
    }

    private int CompareYearMatch(DownloadDecision x, DownloadDecision y)
        => CalculateYearMatchScore(x.RemoteAlbum).CompareTo(CalculateYearMatchScore(y.RemoteAlbum));

    private double CalculateYearMatchScore(RemoteAlbum remoteAlbum)
    {
        if (remoteAlbum.Albums == null || !remoteAlbum.Albums.Any())
            return 0;

        int? parsedYear = remoteAlbum.ParsedAlbumInfo?.ReleaseYear;
        if (!parsedYear.HasValue)
            return 0;

        double totalScore = 0.0;
        foreach (Album? album in remoteAlbum.Albums)
        {
            totalScore += yearMatcher.CalculateYearScore(album.ReleaseDate, parsedYear);
        }

        return totalScore / remoteAlbum.Albums.Count;
    }

    private int CompareQuality(DownloadDecision x, DownloadDecision y)
    {
        int index = GetQualityIndex(x).CompareTo(GetQualityIndex(y));
        if (index != 0 || configService.DownloadPropersAndRepacks == ProperDownloadTypes.DoNotPrefer)
            return index;

        return x.RemoteAlbum.ParsedAlbumInfo.Quality.Revision.CompareTo(y.RemoteAlbum.ParsedAlbumInfo.Quality.Revision);
    }

    private static QualityIndex GetQualityIndex(DownloadDecision decision) =>
        decision.RemoteAlbum.Artist.QualityProfile.Value.GetIndex(decision.RemoteAlbum.ParsedAlbumInfo.Quality.Quality);

    private int CompareProtocol(DownloadDecision x, DownloadDecision y) =>
        GetProtocolRank(x).CompareTo(GetProtocolRank(y));

    private int GetProtocolRank(DownloadDecision decision)
    {
        DelayProfile delayProfile = delayProfileService.BestForTags(decision.RemoteAlbum.Artist.Tags);
        return -1 * delayProfile.Items.FindIndex(i => i.Protocol == decision.RemoteAlbum.Release.DownloadProtocol);
    }
}
