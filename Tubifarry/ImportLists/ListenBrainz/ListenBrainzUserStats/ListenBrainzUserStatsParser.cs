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
        private readonly ListenBrainzUserStatsSettings _settings;
        private readonly Logger _logger;

        public ListenBrainzUserStatsParser(ListenBrainzUserStatsSettings settings)
        {
            _settings = settings;
            _logger = NzbDroneLogger.GetLogger(this);
        }

        public IList<ImportListItemInfo> ParseResponse(ImportListResponse importListResponse)
        {
            List<ImportListItemInfo> items = new();

            if (!PreProcess(importListResponse))
                return items;

            try
            {
                switch (_settings.StatType)
                {
                    case (int)ListenBrainzStatType.Artists:
                        items.AddRange(ParseArtistStats(importListResponse.Content));
                        break;
                    case (int)ListenBrainzStatType.Releases:
                        items.AddRange(ParseReleaseStats(importListResponse.Content));
                        break;
                    case (int)ListenBrainzStatType.ReleaseGroups:
                        items.AddRange(ParseReleaseGroupStats(importListResponse.Content));
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error parsing ListenBrainz user stats response");
                throw new ImportListException(importListResponse, "Error parsing response", ex);
            }

            _logger.Debug($"Parsed {items.Count} items from ListenBrainz user stats");
            return items;
        }

        private IList<ImportListItemInfo> ParseArtistStats(string content)
        {
            List<ImportListItemInfo> items = new();
            ArtistStatsResponse? response = JsonSerializer.Deserialize<ArtistStatsResponse>(content, GetJsonOptions());

            if (response?.Payload?.Artists == null) return items;

            foreach (ArtistStat artist in response.Payload.Artists)
            {
                if (artist.ArtistMbids != null && artist.ArtistMbids.Any())
                {
                    foreach (string? mbid in artist.ArtistMbids.Where(m => !string.IsNullOrEmpty(m)))
                    {
                        items.Add(new ImportListItemInfo
                        {
                            Artist = artist.ArtistName,
                            ArtistMusicBrainzId = mbid
                        });
                    }
                }
            }

            return items;
        }

        private IList<ImportListItemInfo> ParseReleaseStats(string content)
        {
            List<ImportListItemInfo> items = new();
            ReleaseStatsResponse? response = JsonSerializer.Deserialize<ReleaseStatsResponse>(content, GetJsonOptions());

            if (response?.Payload?.Releases == null) return items;

            foreach (ReleaseStat release in response.Payload.Releases)
            {
                if (release.ArtistMbids != null && release.ArtistMbids.Any())
                {
                    foreach (string? mbid in release.ArtistMbids.Where(m => !string.IsNullOrEmpty(m)))
                    {
                        items.Add(new ImportListItemInfo
                        {
                            Artist = release.ArtistName,
                            ArtistMusicBrainzId = mbid
                        });
                    }
                }
            }

            return items;
        }

        private IList<ImportListItemInfo> ParseReleaseGroupStats(string content)
        {
            List<ImportListItemInfo> items = new();
            ReleaseGroupStatsResponse? response = JsonSerializer.Deserialize<ReleaseGroupStatsResponse>(content, GetJsonOptions());

            if (response?.Payload?.ReleaseGroups == null) return items;

            foreach (ReleaseGroupStat releaseGroup in response.Payload.ReleaseGroups)
            {
                if (releaseGroup.ArtistMbids != null && releaseGroup.ArtistMbids.Any())
                {
                    foreach (string? mbid in releaseGroup.ArtistMbids.Where(m => !string.IsNullOrEmpty(m)))
                    {
                        items.Add(new ImportListItemInfo
                        {
                            Artist = releaseGroup.ArtistName,
                            ArtistMusicBrainzId = mbid
                        });
                    }
                }
            }

            return items;
        }

        private JsonSerializerOptions GetJsonOptions()
        {
            return new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
        }

        private bool PreProcess(ImportListResponse importListResponse)
        {
            if (importListResponse.HttpResponse.StatusCode == HttpStatusCode.NoContent)
            {
                _logger.Info("No statistics available yet for this user and time range");
                return false;
            }

            if (importListResponse.HttpResponse.StatusCode != HttpStatusCode.OK)
            {
                throw new ImportListException(importListResponse, "Unexpected StatusCode [{0}]", importListResponse.HttpResponse.StatusCode);
            }

            return true;
        }
    }
}