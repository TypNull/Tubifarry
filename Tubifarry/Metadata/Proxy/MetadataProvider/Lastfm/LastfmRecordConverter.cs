using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tubifarry.Metadata.Proxy.MetadataProvider.Lastfm
{
    public class LastfmTagsConverter : JsonConverter<LastfmTags?>
    {
        public override LastfmTags? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                reader.GetString();
                return null;
            }
            return JsonSerializer.Deserialize<LastfmTags>(ref reader, options);
        }

        public override void Write(Utf8JsonWriter writer, LastfmTags? value, JsonSerializerOptions options)
        {
            if (value == null)
                writer.WriteStringValue("");
            else
                JsonSerializer.Serialize(writer, value, options);
        }
    }

    public class LastfmNumberConverter : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    string? stringValue = reader.GetString();
                    return string.IsNullOrEmpty(stringValue) ? 0 : int.TryParse(stringValue, out int result) ? result : 0;

                case JsonTokenType.Number:
                    return reader.GetInt32();

                case JsonTokenType.Null:
                    return 0;

                default:
                    throw new JsonException($"Unexpected token type: {reader.TokenType}");
            }
        }

        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }

    public class LastfmArtistConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                reader.GetString();
            }
            else if (reader.TokenType == JsonTokenType.StartObject)
            {
                using JsonDocument doc = JsonDocument.ParseValue(ref reader);
                if (doc.RootElement.TryGetProperty("name", out JsonElement nameElement))
                    return nameElement.GetString() ?? "";
                return string.Empty;
            }
            return string.Empty;
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);
    }

    public class LastfmTracksConverter : JsonConverter<LastfmTracks>
    {
        public override LastfmTracks Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException($"Expected StartObject but got {reader.TokenType}");

            List<LastfmTrack> tracks = [];
            bool foundTrackProperty = false;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    break;
                if (reader.TokenType != JsonTokenType.PropertyName)
                    continue;

                string propertyName = reader.GetString()!;
                reader.Read();

                if (propertyName == "track")
                {
                    foundTrackProperty = true;

                    if (reader.TokenType == JsonTokenType.StartArray)
                    {
                        tracks = JsonSerializer.Deserialize<List<LastfmTrack>>(ref reader, options) ?? [];
                    }
                    else if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        LastfmTrack? singleTrack = JsonSerializer.Deserialize<LastfmTrack>(ref reader, options);
                        if (singleTrack != null)
                            tracks.Add(singleTrack);
                    }
                    else if (reader.TokenType == JsonTokenType.Null)
                    {
                        tracks = [];
                    }
                }
                else
                {
                    reader.Skip();
                }
            }

            if (!foundTrackProperty)
                tracks = [];

            return new LastfmTracks(tracks);
        }

        public override void Write(Utf8JsonWriter writer, LastfmTracks value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("track");

            if (value.Tracks.Count == 0)
                writer.WriteNullValue();
            else if (value.Tracks.Count == 1)
                JsonSerializer.Serialize(writer, value.Tracks[0], options);
            else
                JsonSerializer.Serialize(writer, value.Tracks, options);

            writer.WriteEndObject();
        }
    }
}