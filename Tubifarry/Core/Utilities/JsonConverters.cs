using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tubifarry.Core.Utilities
{
    /// <summary>
    /// Custom JSON converter that handles both string and numeric values, converting them to string
    /// </summary>
    public class StringConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString() ?? string.Empty,
                JsonTokenType.Number => reader.GetInt64().ToString(),
                JsonTokenType.True => "true",
                JsonTokenType.False => "false",
                JsonTokenType.Null => string.Empty,
                _ => throw new JsonException($"Cannot convert token type {reader.TokenType} to string")
            };
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
    }

    /// <summary>
    /// Custom JSON converter for flexible float handling
    /// </summary>
    public class FloatConverter : JsonConverter<float>
    {
        public override float Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.TokenType switch
            {
                JsonTokenType.Number => (float)reader.GetDouble(),
                JsonTokenType.String => float.TryParse(reader.GetString(), out float result) ? result :
                    throw new JsonException($"Cannot convert string '{reader.GetString()}' to float"),
                _ => throw new JsonException($"Cannot convert token type {reader.TokenType} to float")
            };
        }

        public override void Write(Utf8JsonWriter writer, float value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }

}