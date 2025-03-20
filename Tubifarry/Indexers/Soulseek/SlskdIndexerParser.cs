using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using System.Text.Json;
using Tubifarry.Core.Model;

namespace Tubifarry.Indexers.Soulseek
{
    public class SlskdIndexerParser : IParseIndexerResponse
    {
        private readonly SlskdIndexer _indexer;
        private readonly Logger _logger;
        private readonly IHttpClient _httpClient;
        private SlskdSettings Settings => _indexer.Settings;

        public SlskdIndexerParser(SlskdIndexer indexer, IHttpClient httpClient)
        {
            _indexer = indexer;
            _logger = NzbDroneLogger.GetLogger(this);
            _httpClient = httpClient;
        }

        public IList<ReleaseInfo> ParseResponse(IndexerResponse indexerResponse)
        {
            List<AlbumData> albumDatas = new();
            try
            {
                using JsonDocument jsonDoc = JsonDocument.Parse(indexerResponse.Content);
                JsonElement root = jsonDoc.RootElement;

                if (!root.TryGetProperty("searchText", out JsonElement searchTextElement) ||
                    !root.TryGetProperty("responses", out JsonElement responsesElement) ||
                    !root.TryGetProperty("id", out JsonElement idElement))
                {
                    _logger.Error("Required fields are missing in the slskd search response.");
                    return new List<ReleaseInfo>();
                }

                SlskdSearchData searchTextData = SlskdSearchData.FromJson(indexerResponse.HttpRequest.ContentSummary);

                foreach (JsonElement responseElement in GetResponses(responsesElement))
                {
                    if (!responseElement.TryGetProperty("fileCount", out JsonElement fileCountElement) || fileCountElement.GetInt32() < searchTextData.MinimumFiles)
                        continue;

                    if (!responseElement.TryGetProperty("files", out JsonElement filesElement))
                        continue;

                    List<SlskdFileData> files = SlskdFileData.GetFiles(filesElement, Settings.OnlyAudioFiles, Settings.IncludeFileExtensions).ToList();

                    foreach (IGrouping<string, SlskdFileData>? directory in files.GroupBy(f => f.Filename?[..f.Filename.LastIndexOf('\\')] ?? "").ToList())
                    {
                        if (string.IsNullOrEmpty(directory.Key))
                            continue;

                        SlskdFolderData folderData = SlskdItemsParser.ParseFolderName(directory.Key).FillWithSlskdData(responseElement);

                        AlbumData albumData = SlskdItemsParser.CreateAlbumData(idElement.GetString()!, directory, searchTextData, folderData, Settings);

                        albumDatas.Add(albumData);
                    }
                }

                if (idElement.GetString() is string searchID)
                    RemoveSearch(searchID, albumDatas.Any() && searchTextData.Interactive);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to parse Slskd search response.");
            }

            return albumDatas.OrderByDescending(x => x.Priotity).Select(a => a.ToReleaseInfo()).ToList();
        }

        private static IEnumerable<JsonElement> GetResponses(JsonElement responsesElement)
        {
            if (responsesElement.ValueKind != JsonValueKind.Array)
                yield break;

            foreach (JsonElement response in responsesElement.EnumerateArray())
                yield return response;
        }

        public void RemoveSearch(string searchId, bool delay = false)
        {
            Task.Run(async () =>
            {
                try
                {
                    if (delay) await Task.Delay(90000);
                    HttpRequest request = new HttpRequestBuilder($"{Settings.BaseUrl}/api/v0/searches/{searchId}").SetHeader("X-API-KEY", Settings.ApiKey).Build();
                    request.Method = HttpMethod.Delete;
                    HttpResponse response = await _httpClient.ExecuteAsync(request);
                }
                catch (HttpException ex)
                {
                    _logger.Error(ex, $"Failed to remove slskd search with ID: {searchId}");
                }
            });
        }
    }
}