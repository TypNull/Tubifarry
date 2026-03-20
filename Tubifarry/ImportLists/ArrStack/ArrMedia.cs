using System.Text.Json.Serialization;

namespace Tubifarry.ImportLists.ArrStack
{
    /// <summary>
    /// Represents a media item from an Arr application (Radarr/Sonarr).
    /// Used for deserializing API responses from Arr applications.
    /// </summary>
    internal record class ArrMedia
    {
        /// <summary>
        /// The title of the media item (movie title, series name, etc.)
        /// </summary>
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Unique identifier for this media item in the Arr application
        /// </summary>
        [JsonPropertyName("id")]
        public int Id { get; set; }

        /// <summary>
        /// File system path where the media is stored
        /// </summary>
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        /// <summary>
        /// Tag IDs assigned to this media item in the Arr application
        /// </summary>
        [JsonPropertyName("tags")]
        public List<int> Tags { get; set; } = [];

        /// <summary>
        /// Returns a string representation for debugging
        /// </summary>
        public override string ToString() => $"{Title} (ID: {Id})";
    }

    internal record ArrTag
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("label")]
        public string Label { get; set; } = string.Empty;
    }
}