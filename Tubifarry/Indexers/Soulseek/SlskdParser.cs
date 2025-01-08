using NLog;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Parser.Model;
using System.Text.Json;
using Tubifarry.Core;

namespace NzbDrone.Core.Indexers.Soulseek
{
    public class SlskdParser : IParseIndexerResponse
    {
        private readonly Logger _logger;

        public SlskdParser() => _logger = NzbDroneLogger.GetLogger(this);


        public IList<ReleaseInfo> ParseResponse(IndexerResponse indexerResponse)
        {
            List<ReleaseInfo> releases = new();

            try
            {
                using JsonDocument jsonDoc = JsonDocument.Parse(indexerResponse.Content);
                JsonElement root = jsonDoc.RootElement;

                if (!root.TryGetProperty("searchText", out JsonElement searchTextElement) ||
                    !root.TryGetProperty("responses", out JsonElement responsesElement))
                {
                    _logger.Warn("Required fields are missing in the slskd search response.");
                    return releases;
                }

                string? searchText = searchTextElement.GetString();
                SlskdSearchTextData searchTextData = SlskdSearchTextData.ParseSearchText(searchText ?? string.Empty);

                foreach (JsonElement responseElement in GetResponses(responsesElement))
                {
                    if (!responseElement.TryGetProperty("fileCount", out JsonElement fileCountElement) || fileCountElement.GetInt32() == 0)
                        continue;

                    string? username = responseElement.TryGetProperty("username", out JsonElement usernameElement) ? usernameElement.GetString() : "Unknown User";

                    if (!responseElement.TryGetProperty("files", out JsonElement filesElement))
                        continue;

                    List<SlskdFileData> files = SlskdFileData.GetFiles(filesElement).ToList();
                    List<IGrouping<string, SlskdFileData>> directories = files
                        .GroupBy(f => f.Filename?[..f.Filename.LastIndexOf('\\')] ?? "")
                        .ToList();

                    foreach (IGrouping<string, SlskdFileData>? directory in directories)
                    {
                        if (string.IsNullOrEmpty(directory.Key))
                            continue;

                        SlskdFolderData folderData = SlskdFolderData.ParseFolderName(directory.Key);
                        AlbumData albumData = CreateAlbumData(directory, username ?? "Unknown Username", searchTextData, folderData);

                        releases.Add(albumData.ToReleaseInfo());
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to parse Slskd search response.");
            }

            return releases.OrderByDescending(r => r.Size).ToList();
        }

        private AlbumData CreateAlbumData(IGrouping<string, SlskdFileData> directory, string username, SlskdSearchTextData searchTextData, SlskdFolderData folderData)
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

            return new AlbumData("Slskd")
            {
                AlbumId = hash,
                ArtistName = artist ?? "Unknown Artis",
                AlbumName = album ?? "Unknown Album",
                ReleaseDate = folderData.Year,
                ReleaseDateTime = (string.IsNullOrEmpty(folderData.Year) || !int.TryParse(folderData.Year, out int yearInt) ? DateTime.MinValue : new DateTime(yearInt, 1, 1)),
                Codec = AudioFormatHelper.GetAudioFormatFromCodec(mostCommonExtension ?? string.Empty),
                Bitrate = bitRate ?? 0,
                Size = totalSize,
                Duration = totalDuration
            };
        }

        private string? GetMostCommonExtension(IEnumerable<SlskdFileData> files)
        {
            List<string?> extensions = files
                .Select(f => string.IsNullOrEmpty(f.Extension) ? Path.GetExtension(f.Filename)?.TrimStart('.').ToLowerInvariant() : f.Extension)
                .Where(ext => !string.IsNullOrEmpty(ext))
                .ToList();

            if (!extensions.Any())
                return null;

            string? mostCommonExtension = extensions
                .GroupBy(ext => ext)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault();

            _logger.Trace($"Most common extension: {mostCommonExtension}");
            return mostCommonExtension;
        }

        private static IEnumerable<JsonElement> GetResponses(JsonElement responsesElement)
        {
            if (responsesElement.ValueKind != JsonValueKind.Array)
                yield break;

            foreach (JsonElement response in responsesElement.EnumerateArray())
                yield return response;
        }
    }
}