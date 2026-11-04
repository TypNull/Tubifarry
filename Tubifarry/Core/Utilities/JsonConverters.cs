using System.Globalization;
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

    /// <summary>
    /// Custom JSON converter that handles both boolean and string boolean values
    /// </summary>
    public class BooleanConverter : JsonConverter<bool>
    {
        public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.TokenType switch
            {
                JsonTokenType.True => true,
                JsonTokenType.False => false,
                JsonTokenType.String => bool.TryParse(reader.GetString(), out bool result) && result,
                JsonTokenType.Number => reader.GetInt32() != 0,
                _ => throw new JsonException($"Cannot convert token type {reader.TokenType} to boolean")
            };
        }

        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteBooleanValue(value);
    }

    /// <summary>
    /// Lenient date converter
    /// </summary>
    public class LenientDateTimeConverter : JsonConverter<DateTime?>
    {
        public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                reader.Skip();
                return null;
            }

            string? value = reader.GetString();
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("0000"))
                return null;

            return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime result) && result != DateTime.MinValue ? result : null;
        }

        public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
        {
            if (value.HasValue)
                writer.WriteStringValue(value.Value);
            else
                writer.WriteNullValue();
        }
    }

    /// <summary>
    /// Converts Unix timestamps (milliseconds) to DateTime
    /// </summary>
    internal class UnixTimestampConverter : JsonConverter<DateTime?>
    {
        private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
                return null;

            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out long unixTime))
                return UnixEpoch.AddMilliseconds(unixTime);

            if (reader.TokenType == JsonTokenType.String)
            {
                string? dateStr = reader.GetString();
                if (long.TryParse(dateStr, out long unixTimeParsed))
                    return UnixEpoch.AddMilliseconds(unixTimeParsed);
            }

            return null;
        }

        public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
        {
            if (value.HasValue)
            {
                long unixTime = (long)(value.Value.ToUniversalTime() - UnixEpoch).TotalMilliseconds;
                writer.WriteNumberValue(unixTime);
            }
            else
            {
                writer.WriteNullValue();
            }
        }
    }
}