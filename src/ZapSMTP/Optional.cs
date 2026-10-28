using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZapSMTP
{
    /// <summary>
    /// A request field that may be left out or sent with a value, null included: <c>DomainId = null</c>
    /// clears a key's domain, while leaving <c>DomainId</c> unset changes nothing.
    /// </summary>
    /// <typeparam name="T">The field's type.</typeparam>
    [JsonConverter(typeof(OptionalConverterFactory))]
    public readonly struct Optional<T>
    {
        /// <summary>A field set to <paramref name="value"/>, which may be null.</summary>
        /// <param name="value">The value to send.</param>
        public Optional(T value)
        {
            Value = value;
            HasValue = true;
        }

        /// <summary>Whether the field is set, to a value or to null.</summary>
        public bool HasValue { get; }

        /// <summary>The value to send; the type's default when the field is unset.</summary>
        public T Value { get; }

        /// <summary>A field set to <paramref name="value"/>.</summary>
        /// <param name="value">The value to send, null included.</param>
        public static implicit operator Optional<T>(T value) => new(value);
    }

    internal sealed class OptionalConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) =>
            typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Optional<>);

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(OptionalConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()))!;
    }

    internal sealed class OptionalConverter<T> : JsonConverter<Optional<T>>
    {
        public override Optional<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(JsonSerializer.Deserialize<T>(ref reader, options)!);

        public override void Write(Utf8JsonWriter writer, Optional<T> value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.Value, options);
    }
}
