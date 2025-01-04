using System.Text.Json.Serialization;

namespace NzbDrone.Core.ImportLists.ArrStack
{
    public class ArrMedia
    {
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;
    }
}
