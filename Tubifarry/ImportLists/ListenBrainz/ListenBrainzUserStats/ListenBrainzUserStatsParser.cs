using NLog;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Exceptions;
using NzbDrone.Core.Parser.Model;
using System.Net;
using System.Text.Json;

namespace Tubifarry.ImportLists.ListenBrainz.ListenBrainzUserStats
{
    public class ListenBrainzUserStatsParser : IParseImportListResponse
    {
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
        private readonly ListenBrainzUserStatsSettings _settings;
        private readonly Logger _logger;

        public ListenBrainzUserStatsParser(ListenBrainzUserStatsSettings settings)
        {
            _settings = settings;
            _logger = NzbDroneLogger.GetLogger(this);
        }

        public IList<ImportListItemInfo> ParseResponse(ImportListResponse importListResponse)
        {
            if (!PreProcess(importListResponse))
                return [];

            try
            {
                List<ImportListItemInfo> items = _settings.StatType switch
                {
                    (int)ListenBrainzStatType.Artists => ParseArtistStats(importListResponse.Content),
                    (int)ListenBrainzStatType.Releases => ParseReleaseStats(importListResponse.Content),
                    (int)ListenBrainzStatType.ReleaseGroups => ParseReleaseGroupStats(importListResponse.Content),
                    _ => []
                };

                _logger.Debug("Successfully parsed {0} items from ListenBrainz user stats", items.Count);
                return items;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to parse ListenBrainz user stats response");
                throw new ImportListException(importListResponse, "Failed to parse response", ex);
            }
        }

        private List<ImportListItemInfo> ParseArtistStats(string content)
        {
            ArtistStatsResponse? response = JsonSerializer.Deserialize<ArtistStatsResponse>(content, _jsonOptions);
            IReadOnlyList<ArtistStat>? artists = response?.Payload?.Artists;

            if (artists?.Any() != true)
            {
                _logger.Debug("No artist stats found");
                return [];
            }

            return artists
                .Where(artist => !string.IsNullOrWhiteSpace(artist.ArtistName))
                .Select(artist => new ImportListItemInfo
                {
                    Artist = artist.ArtistName,
                    ArtistMusicBrainzId = artist.ArtistMbid
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.Artist))
                .ToList();
        }

        private List<ImportListItemInfo> ParseReleaseStats(string content)
        {
            ReleaseStatsResponse? response = JsonSerializer.Deserialize<ReleaseStatsResponse>(content, _jsonOptions);
            IReadOnlyList<ReleaseStat>? releases = response?.Payload?.Releases;

            if (releases?.Any() != true)
            {
                _logger.Debug("No release stats found");
                return [];
            }

            return releases
                .Where(release => !string.IsNullOrWhiteSpace(release.ReleaseName) &&
                                 !string.IsNullOrWhiteSpace(release.ArtistName))
                .Select(release => new ImportListItemInfo
                {
                    Album = release.ReleaseName,
                    Artist = release.ArtistName,
                    ArtistMusicBrainzId = release.ArtistMbids?.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m)),
                    AlbumMusicBrainzId = release.ReleaseMbid
                })
                .ToList();
        }

        private List<ImportListItemInfo> ParseReleaseGroupStats(string content)
        {
            ReleaseGroupStatsResponse? response = JsonSerializer.Deserialize<ReleaseGroupStatsResponse>(content, _jsonOptions);
            IReadOnlyList<ReleaseGroupStat>? releaseGroups = response?.Payload?.ReleaseGroups;

            if (releaseGroups?.Any() != true)
            {
                _logger.Debug("No release group stats found");
                return [];
            }

            return releaseGroups
                .Where(rg => !string.IsNullOrWhiteSpace(rg.ReleaseGroupName) &&
                            !string.IsNullOrWhiteSpace(rg.ArtistName))
                .Select(rg => new ImportListItemInfo
                {
                    Album = rg.ReleaseGroupName,
                    Artist = rg.ArtistName,
                    ArtistMusicBrainzId = rg.ArtistMbids?.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m))
                })
                .ToList();
        }

        private bool PreProcess(ImportListResponse importListResponse)
        {
            if (importListResponse.HttpResponse.StatusCode == HttpStatusCode.NoContent)
            {
                _logger.Info("No statistics available for this user and time range");
                return false;
            }

            if (importListResponse.HttpResponse.StatusCode != HttpStatusCode.OK)
            {
                throw new ImportListException(importListResponse, "Unexpected status code {0}", importListResponse.HttpResponse.StatusCode);
            }

            return true;
        }
    }
}