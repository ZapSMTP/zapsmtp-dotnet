using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZapSMTP.Internal
{
    /// <summary>The SDK's JSON settings and helpers.</summary>
    internal static class ZapJson
    {
        public static readonly JsonSerializerOptions Options = CreateOptions();

        private static JsonSerializerOptions CreateOptions()
        {
            var options = new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                // bodies go to the API, never into HTML: keep '+', '<' and non-ASCII text as they are
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            options.Converters.Add(new LenientConverterFactory());
            return options;
        }

        /// <summary>The JSON in <paramref name="text"/>, or null when it is empty or not JSON.</summary>
        public static JsonElement? TryParse(string text)
        {
            if (text.Length == 0) return null;
            try
            {
                using var doc = JsonDocument.Parse(text);
                return doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>A model decoded from a response body; an empty one when the body is not a JSON object.</summary>
        public static T Decode<T>(string text) where T : class, new() =>
            TryParse(text) is { ValueKind: JsonValueKind.Object } root ? root.Deserialize<T>(Options)! : new T();

        /// <summary>The string property <paramref name="name"/> of an object, else null.</summary>
        public static string? String(JsonElement? element, string name) =>
            element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

        /// <summary>The object property <paramref name="name"/> of an object, else null.</summary>
        public static JsonElement? Object(JsonElement? element, string name) =>
            element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
                ? v
                : null;

        public static bool IsObjectType(Type type) =>
            typeof(ZapObject).IsAssignableFrom(type) || typeof(ZapParams).IsAssignableFrom(type);
    }

    /// <summary>
    /// Decodes the leaf types of responses without ever failing: a value of another JSON type becomes
    /// null (or is converted, such as a number sent where a string was expected).
    /// </summary>
    internal sealed class LenientConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert)
        {
            if (typeToConvert == typeof(string) || typeToConvert == typeof(long?) || typeToConvert == typeof(double?) ||
                typeToConvert == typeof(bool?) || typeToConvert == typeof(DateTimeOffset?)) return true;
            if (!typeToConvert.IsGenericType) return false;
            var def = typeToConvert.GetGenericTypeDefinition();
            return def == typeof(IReadOnlyList<>) ||
                (def == typeof(IReadOnlyDictionary<,>) && typeToConvert.GetGenericArguments()[0] == typeof(string));
        }

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            if (typeToConvert == typeof(string)) return new LenientStringConverter();
            if (typeToConvert == typeof(long?)) return new LenientInt64Converter();
            if (typeToConvert == typeof(double?)) return new LenientDoubleConverter();
            if (typeToConvert == typeof(bool?)) return new LenientBooleanConverter();
            if (typeToConvert == typeof(DateTimeOffset?)) return new LenientDateTimeOffsetConverter();
            var args = typeToConvert.GetGenericArguments();
            var converter = typeToConvert.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)
                ? typeof(LenientListConverter<>).MakeGenericType(args[0])
                : typeof(LenientMapConverter<>).MakeGenericType(args[1]);
            return (JsonConverter)Activator.CreateInstance(converter)!;
        }
    }

    internal sealed class LenientStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    return reader.GetString();
                case JsonTokenType.Number:
                case JsonTokenType.True:
                case JsonTokenType.False:
                    using (var doc = JsonDocument.ParseValue(ref reader)) return doc.RootElement.GetRawText();
                default:
                    reader.Skip();
                    return null;
            }
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
    }

    internal sealed class LenientInt64Converter : JsonConverter<long?>
    {
        public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Number:
                    return reader.TryGetInt64(out var n) ? n : null;
                case JsonTokenType.String:
                    return long.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : null;
                default:
                    reader.Skip();
                    return null;
            }
        }

        public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options) => writer.WriteNumberValue(value.GetValueOrDefault());
    }

    internal sealed class LenientDoubleConverter : JsonConverter<double?>
    {
        public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Number:
                    return Finite(reader.GetDouble());
                case JsonTokenType.String:
                    return double.TryParse(reader.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? Finite(d) : null;
                default:
                    reader.Skip();
                    return null;
            }
        }

        public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options) => writer.WriteNumberValue(value.GetValueOrDefault());

        // 1e999, NaN and the infinities are no number a response can carry: null, like any other surprise
        private static double? Finite(double d) => Math.Abs(d) <= double.MaxValue ? d : null;
    }

    internal sealed class LenientBooleanConverter : JsonConverter<bool?>
    {
        public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.True:
                case JsonTokenType.False:
                    return reader.GetBoolean();
                case JsonTokenType.String:
                    return bool.TryParse(reader.GetString(), out var b) ? b : null;
                default:
                    reader.Skip();
                    return null;
            }
        }

        public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options) => writer.WriteBooleanValue(value.GetValueOrDefault());
    }

    internal sealed class LenientDateTimeOffsetConverter : JsonConverter<DateTimeOffset?>
    {
        public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String &&
                DateTimeOffset.TryParse(reader.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)) return d;
            reader.Skip();
            return null;
        }

        // RFC 3339, as the API writes times
        public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.GetValueOrDefault().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture));
    }

    /// <summary>Reads one list or map element: objects only where an object is expected; nulls are dropped.</summary>
    internal static class LenientElement
    {
        public static bool TryRead<T>(ref Utf8JsonReader reader, JsonSerializerOptions options, out T value)
        {
            if (ZapJson.IsObjectType(typeof(T)) && reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                value = default!;
                return false;
            }
            value = JsonSerializer.Deserialize<T>(ref reader, options)!;
            return value is not null;
        }
    }

    internal sealed class LenientListConverter<T> : JsonConverter<IReadOnlyList<T>>
    {
        public override IReadOnlyList<T>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                reader.Skip();
                return null;
            }
            var list = new List<T>();
            // the serializer hands a converter the whole value, so Read() always succeeds here
            for (reader.Read(); reader.TokenType != JsonTokenType.EndArray; reader.Read())
            {
                if (LenientElement.TryRead<T>(ref reader, options, out var item)) list.Add(item);
            }
            return list;
        }

        public override void Write(Utf8JsonWriter writer, IReadOnlyList<T> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var item in value) JsonSerializer.Serialize(writer, item, options);
            writer.WriteEndArray();
        }
    }

    internal sealed class LenientMapConverter<T> : JsonConverter<IReadOnlyDictionary<string, T>>
    {
        public override IReadOnlyDictionary<string, T>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                return null;
            }
            var map = new Dictionary<string, T>();
            for (reader.Read(); reader.TokenType == JsonTokenType.PropertyName; reader.Read())
            {
                var key = reader.GetString()!;
                reader.Read();
                if (LenientElement.TryRead<T>(ref reader, options, out var item)) map[key] = item;
            }
            return map;
        }

        public override void Write(Utf8JsonWriter writer, IReadOnlyDictionary<string, T> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var pair in value)
            {
                writer.WritePropertyName(pair.Key);
                JsonSerializer.Serialize(writer, pair.Value, options);
            }
            writer.WriteEndObject();
        }
    }

    /// <summary>
    /// Decodes a nested response object leniently: any other JSON value leaves the property null.
    /// Used by the generated models.
    /// </summary>
    /// <typeparam name="T">The object's model.</typeparam>
    internal sealed class LenientObjectConverter<T> : JsonConverter<T> where T : class
    {
        public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                return null;
            }
            return JsonSerializer.Deserialize<T>(ref reader, options);
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => JsonSerializer.Serialize(writer, value, options);
    }
}
