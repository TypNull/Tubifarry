﻿using FuzzySharp;
using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using System.Text.Json;
using Tubifarry.Core.Model;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Indexers.Soulseek
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
            List<AlbumData> albumDatas = new();
            try
            {
                using JsonDocument jsonDoc = JsonDocument.Parse(indexerResponse.Content);
                JsonElement root = jsonDoc.RootElement;

                if (!root.TryGetProperty("searchText", out JsonElement searchTextElement) || !root.TryGetProperty("responses", out JsonElement responsesElement) || !root.TryGetProperty("id", out JsonElement idElement))
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
                        SlskdFolderData folderData = SlskdFolderData.ParseFolderName(directory.Key).FillWithSlskdData(responseElement);
                        AlbumData albumData = CreateAlbumData(idElement.GetString()!, directory, searchTextData, folderData);
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

        private AlbumData CreateAlbumData(string searchId, IGrouping<string, SlskdFileData> directory, SlskdSearchData searchData, SlskdFolderData folderData)
        {
            string hash = $"{folderData.Username} {directory.Key}".GetHashCode().ToString("X");

            bool isArtistContained = !string.IsNullOrEmpty(searchData.Artist) && Fuzz.PartialRatio(directory.Key, searchData.Artist) > 80;
            bool isAlbumContained = !string.IsNullOrEmpty(searchData.Album) && Fuzz.PartialRatio(directory.Key, searchData.Album) > 80;

            string? artist = isArtistContained ? searchData.Artist : folderData.Artist;
            string? album = isAlbumContained ? searchData.Album : folderData.Album;

            artist = string.IsNullOrEmpty(artist) ? searchData.Artist : artist;
            album = string.IsNullOrEmpty(album) ? searchData.Album : album;

            string? mostCommonExtension = GetMostCommonExtension(directory);

            long totalSize = directory.Sum(f => f.Size);
            int totalDuration = directory.Sum(f => f.Length ?? 0);

            int? mostCommonBitRate = directory.GroupBy(f => f.BitRate).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;

            int? mostCommonBitDepth = directory.GroupBy(f => f.BitDepth).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;


            if (!mostCommonBitRate.HasValue && totalDuration > 0)
                mostCommonBitRate = (int)(totalSize * 8 / (totalDuration * 1000));

            List<SlskdFileData>? filesToDownload = directory.GroupBy(f => f.Filename?[..f.Filename.LastIndexOf('\\')]).FirstOrDefault(g => g.Key == directory.Key)?.ToList();
            AudioFormat codec = AudioFormatHelper.GetAudioCodecFromExtension(mostCommonExtension ?? string.Empty);
            return new AlbumData("Slskd")
            {
                AlbumId = $"/api/v0/transfers/downloads/{folderData.Username}",
                ArtistName = artist ?? "Unknown Artist",
                AlbumName = album ?? "Unknown Album",
                ReleaseDate = folderData.Year,
                ReleaseDateTime = string.IsNullOrEmpty(folderData.Year) || !int.TryParse(folderData.Year, out int yearInt) ? DateTime.MinValue : new DateTime(yearInt, 1, 1),
                Codec = codec,
                BitDepth = mostCommonBitDepth ?? 0,
                Bitrate = (codec == AudioFormat.MP3 ? AudioFormatHelper.RoundToStandardBitrate(mostCommonBitRate ?? 0) : mostCommonBitRate) ?? 0,
                Size = totalSize,
                Priotity = folderData.CalculatePriority(),
                CustomString = JsonConvert.SerializeObject(filesToDownload),
                InfoUrl = $"{(string.IsNullOrEmpty(Settings.ExternalUrl) ? Settings.BaseUrl : Settings.ExternalUrl)}/searches/{searchId}",
                ExtraInfo = $"User: {folderData.Username}",
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