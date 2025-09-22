using NLog;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Exceptions;
using NzbDrone.Core.Parser.Model;
using System.Net;
using System.Text.Json;

namespace Tubifarry.ImportLists.ListenBrainz.ListenBrainzCFRecommendations
{
    public class ListenBrainzCFRecommendationsParser : IParseImportListResponse
    {
        private readonly Logger _logger;

        public ListenBrainzCFRecommendationsParser()
        {
            _logger = NzbDroneLogger.GetLogger(this);
        }

        public IList<ImportListItemInfo> ParseResponse(ImportListResponse importListResponse)
        {
            if (!PreProcess(importListResponse))
                return new List<ImportListItemInfo>();

            try
            {
                IList<ImportListItemInfo> items = ParseRecordingRecommendations(importListResponse.Content);
                _logger.Trace("Successfully parsed {0} recording recommendations", items.Count);
                return items;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to parse ListenBrainz recording recommendations");
                throw new ImportListException(importListResponse, "Failed to parse response", ex);
            }
        }

        private IList<ImportListItemInfo> ParseRecordingRecommendations(string content)
        {
            RecordingRecommendationResponse? response = JsonSerializer.Deserialize<RecordingRecommendationResponse>(content, GetJsonOptions());
            IReadOnlyList<RecordingRecommendation>? recommendations = response?.Payload?.Mbids;

            if (recommendations?.Any() != true)
            {
                _logger.Debug("No recording recommendations available");
                return new List<ImportListItemInfo>();
            }

            _logger.Trace("Processing {0} recording recommendations", recommendations.Count);

            return recommendations
                .Where(r => !string.IsNullOrWhiteSpace(r.RecordingMbid))
                .Select(r => new ImportListItemInfo
                {
                    AlbumMusicBrainzId = r.RecordingMbid,
                    Album = r.RecordingMbid
                })
                .ToList();
        }

        private static JsonSerializerOptions GetJsonOptions() => new()
        {
            PropertyNameCaseInsensitive = true
        };

        private bool PreProcess(ImportListResponse importListResponse)
        {
            if (importListResponse.HttpResponse.StatusCode == HttpStatusCode.NoContent)
            {
                _logger.Info("No recording recommendations available for this user");
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
