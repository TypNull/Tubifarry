using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Parser.Model;
using System.Text.Json;
using Tubifarry.Core;

namespace NzbDrone.Core.Indexers.Soulseek
{
    public class SlskdParser : IParseIndexerResponse
    {
        private readonly SlskdIndexer _indexer;
        private readonly Logger _logger;
        private readonly IHttpClient _httpClient;
        private SlskdSettings Settings => _indexer.Settings;

        public SlskdParser(SlskdIndexer indexer, IHttpClient htmlClient)
        {
            _indexer = indexer;
            _logger = NzbDroneLogger.GetLogger(this);
            _httpClient = htmlClient;
        }

        public IList<ReleaseInfo> ParseResponse(IndexerResponse indexerResponse)
        {
            List<ReleaseInfo> releases = new();
            try
            {
                using JsonDocument jsonDoc = JsonDocument.Parse(indexerResponse.Content);
                JsonElement root = jsonDoc.RootElement;

                if (!root.TryGetProperty("searchText", out JsonElement searchTextElement) || !root.TryGetProperty("responses", out JsonElement responsesElement) || !root.TryGetProperty("id", out JsonElement idElement))
                {
                    _logger.Error("Required fields are missing in the slskd search response.");
                    return releases;
                }

                SlskdSearchTextData searchTextData = SlskdSearchTextData.ParseSearchText(searchTextElement.GetString() ?? string.Empty);

                foreach (JsonElement responseElement in GetResponses(responsesElement))
                {
                    if (!responseElement.TryGetProperty("fileCount", out JsonElement fileCountElement) || fileCountElement.GetInt32() == 0)
                        continue;
                    if (!responseElement.TryGetProperty("files", out JsonElement filesElement))
                        continue;

                    string username = responseElement.TryGetProperty("username", out JsonElement usernameElement) ? usernameElement.GetString() ?? "Unknown User" : "Unknown User";

                    List<SlskdFileData> files = SlskdFileData.GetFiles(filesElement).ToList();
                    List<IGrouping<string, SlskdFileData>> directories = files.GroupBy(f => f.Filename?[..f.Filename.LastIndexOf('\\')] ?? "").ToList();

                    foreach (IGrouping<string, SlskdFileData>? directory in directories)
                    {
                        if (string.IsNullOrEmpty(directory.Key))
                            continue;

                        SlskdFolderData folderData = SlskdFolderData.ParseFolderName(directory.Key);
                        AlbumData albumData = CreateAlbumData(idElement.GetString()!, directory, username, searchTextData, folderData);
                        releases.Add(albumData.ToReleaseInfo());
                    }
                }
                if (idElement.GetString() is string searchID)
                    RemoveSearch(searchID).Wait();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to parse Slskd search response.");
            }
            return releases.OrderByDescending(r => r.Size).ToList();
        }

        private AlbumData CreateAlbumData(string searchId, IGrouping<string, SlskdFileData> directory, string username, SlskdSearchTextData searchTextData, SlskdFolderData folderData)
        {
            string hash = $"{username} {directory.Key}".GetHashCode().ToString("X");

            bool isArtistContained = !string.IsNullOrEmpty(searchTextData.Artist) && directory.Key.Contains(searchTextData.Artist, StringComparison.OrdinalIgnoreCase);
            bool isAlbumContained = !string.IsNullOrEmpty(searchTextData.Album) && directory.Key.Contains(searchTextData.Album, StringComparison.OrdinalIgnoreCase);

            string? artist = isArtistContained ? searchTextData.Artist : folderData.Artist;
            string? album = isAlbumContained ? searchTextData.Album : folderData.Album;

            artist = string.IsNullOrEmpty(artist) ? searchTextData.Artist : artist;
            album = string.IsNullOrEmpty(album) ? searchTextData.Album : album;

            string? mostCommonExtension = GetMostCommonExtension(directory);

            long totalSize = directory.Sum(f => f.Size);
            int totalDuration = directory.Sum(f => f.Length ?? 0);

            int? bitRate = directory.First().BitRate;
            if (!bitRate.HasValue && totalDuration > 0)
                bitRate = (int)((totalSize * 8) / (totalDuration * 1000));

            List<SlskdFileData>? filesToDownload = directory.GroupBy(f => f.Filename?[..f.Filename.LastIndexOf('\\')]).FirstOrDefault(g => g.Key == directory.Key)?.ToList();

            return new AlbumData("Slskd")
            {
                AlbumId = $"/api/v0/transfers/downloads/{username}",
                ArtistName = artist ?? "Unknown Artist",
                AlbumName = album ?? "Unknown Album",
                ReleaseDate = folderData.Year,
                ReleaseDateTime = (string.IsNullOrEmpty(folderData.Year) || !int.TryParse(folderData.Year, out int yearInt) ? DateTime.MinValue : new DateTime(yearInt, 1, 1)),
                Codec = AudioFormatHelper.GetAudioFormatFromCodec(mostCommonExtension ?? string.Empty),
                Bitrate = bitRate ?? 0,
                Size = totalSize,
                CustomString = JsonConvert.SerializeObject(filesToDownload),
                InfoUrl = $"{(string.IsNullOrEmpty(Settings.ExternalUrl) ? Settings.BaseUrl : Settings.ExternalUrl)}/searches/{searchId}",
                Duration = totalDuration
            };
        }

        private static string? GetMostCommonExtension(IEnumerable<SlskdFileData> files)
        {
            List<string?> extensions = files.Select(f => string.IsNullOrEmpty(f.Extension) ? Path.GetExtension(f.Filename)?.TrimStart('.')
            .ToLowerInvariant() : f.Extension).Where(ext => !string.IsNullOrEmpty(ext)).ToList();
            return extensions.Any() ? extensions.GroupBy(ext => ext).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() : null;
        }

        private static IEnumerable<JsonElement> GetResponses(JsonElement responsesElement)
        {
            if (responsesElement.ValueKind != JsonValueKind.Array)
                yield break;

            foreach (JsonElement response in responsesElement.EnumerateArray())
                yield return response;
        }

        public async Task RemoveSearch(string searchId)
        {
            try
            {
                HttpRequest request = new HttpRequestBuilder($"{Settings.BaseUrl}/api/v0/searches/{searchId}").SetHeader("X-API-KEY", Settings.ApiKey).Build();

                request.Method = HttpMethod.Delete;
                HttpResponse response = await _httpClient.ExecuteAsync(request);
            }
            catch (HttpException ex)
            {
                _logger.Error(ex, $"Failed to remove slskd search with ID: {searchId}");
            }
        }

    }
}