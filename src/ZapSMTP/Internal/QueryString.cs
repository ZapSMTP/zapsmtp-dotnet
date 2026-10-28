using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ZapSMTP.Internal
{
    /// <summary>A query string in order: arrays repeat their key, maps become key[name]=value.</summary>
    internal sealed class QueryString
    {
        private readonly List<KeyValuePair<string, string>> _pairs;

        public QueryString()
        {
            _pairs = new List<KeyValuePair<string, string>>();
        }

        private QueryString(List<KeyValuePair<string, string>> pairs)
        {
            _pairs = pairs;
        }

        public void Add(string key, string? value)
        {
            if (value is not null) _pairs.Add(new KeyValuePair<string, string>(key, value));
        }

        public void Add(string key, int? value) => Add(key, value?.ToString(CultureInfo.InvariantCulture));

        public void Add(string key, bool? value) => Add(key, value is { } b ? (b ? "true" : "false") : null);

        public void Add(string key, IReadOnlyList<string>? values)
        {
            if (values is null) return;
            foreach (var v in values) Add(key, v);
        }

        public void Add(string key, IReadOnlyDictionary<string, string>? map)
        {
            if (map is null) return;
            foreach (var pair in map) Add($"{key}[{pair.Key}]", pair.Value);
        }

        /// <summary>Parameters the SDK does not know (ZapParams.AdditionalProperties).</summary>
        public void AddExtra(IDictionary<string, object?>? extra)
        {
            if (extra is null) return;
            foreach (var pair in extra) Add(pair.Key, Format(pair.Value));
        }

        private static string? Format(object? value) => value switch
        {
            null => null,
            string s => s,
            bool b => b ? "true" : "false",
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
            JsonElement e => e.GetRawText(),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };

        public bool Has(string key) => _pairs.Exists(p => p.Key == key);

        /// <summary>A copy with <paramref name="key"/> set to <paramref name="value"/>, in its place or at the end.</summary>
        public QueryString With(string key, string value)
        {
            var pairs = new List<KeyValuePair<string, string>>(_pairs);
            var at = pairs.FindIndex(p => p.Key == key);
            pairs.RemoveAll(p => p.Key == key);
            pairs.Insert(at < 0 ? pairs.Count : at, new KeyValuePair<string, string>(key, value));
            return new QueryString(pairs);
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            foreach (var pair in _pairs)
            {
                if (sb.Length > 0) sb.Append('&');
                sb.Append(Encode(pair.Key)).Append('=').Append(Encode(pair.Value));
            }
            return sb.ToString();
        }

        // application/x-www-form-urlencoded, as browsers and URLSearchParams write it
        private static string Encode(string s) => Uri.EscapeDataString(s).Replace("%20", "+");
    }
}
