using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZapSMTP.Internal;

namespace ZapSMTP
{
    /// <summary>
    /// Webhook signature verification. Every request ZapSMTP sends carries
    /// <c>Zap-Signature: t=&lt;unix seconds&gt;,v1=&lt;hex&gt;</c>, where v1 is HMAC-SHA256 over
    /// "&lt;t&gt;.&lt;raw body&gt;" keyed with the whole secret, its whsec_ prefix included. Two v1 entries are
    /// sent for 24 hours after a secret rotation.
    /// </summary>
    public static class Webhooks
    {
        /// <summary>The oldest signature accepted by default: five minutes.</summary>
        public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

        /// <summary>The header that carries the signature.</summary>
        public const string SignatureHeader = "Zap-Signature";

        /// <summary>
        /// Checks a webhook request's signature and returns its event. A webhook with batch delivery sends an
        /// array of events: use <see cref="VerifyEvents(string, string, string, TimeSpan?, DateTimeOffset?)"/>.
        /// </summary>
        /// <param name="payload">The request body exactly as received, before any JSON parsing.</param>
        /// <param name="signature">The Zap-Signature header's value.</param>
        /// <param name="secret">The webhook's signing secret (whsec_...).</param>
        /// <param name="tolerance">The oldest signature accepted (default five minutes).</param>
        /// <param name="now">The time to check against (default now).</param>
        /// <returns>The event.</returns>
        /// <exception cref="WebhookVerificationException">The signature is missing, wrong or too old, or the body is not JSON.</exception>
        public static WebhookEvent Verify(string payload, string? signature, string secret, TimeSpan? tolerance = null, DateTimeOffset? now = null) =>
            Verify(Encoding.UTF8.GetBytes(payload ?? throw new ArgumentNullException(nameof(payload))), signature, secret, tolerance, now);

        /// <summary>Checks a webhook request's signature, found in its headers, and returns its event.</summary>
        /// <param name="payload">The request body exactly as received, before any JSON parsing.</param>
        /// <param name="headers">The request's headers; the name is matched case-insensitively.</param>
        /// <param name="secret">The webhook's signing secret (whsec_...).</param>
        /// <param name="tolerance">The oldest signature accepted (default five minutes).</param>
        /// <param name="now">The time to check against (default now).</param>
        /// <returns>The event.</returns>
        /// <exception cref="WebhookVerificationException">The signature is missing, wrong or too old, or the body is not JSON.</exception>
        public static WebhookEvent Verify(string payload, IEnumerable<KeyValuePair<string, string>> headers, string secret, TimeSpan? tolerance = null, DateTimeOffset? now = null) =>
            Verify(payload, Find(headers), secret, tolerance, now);

        /// <summary>Checks a webhook request's signature and returns its event.</summary>
        /// <param name="payload">The request body's bytes exactly as received.</param>
        /// <param name="signature">The Zap-Signature header's value.</param>
        /// <param name="secret">The webhook's signing secret (whsec_...).</param>
        /// <param name="tolerance">The oldest signature accepted (default five minutes).</param>
        /// <param name="now">The time to check against (default now).</param>
        /// <returns>The event.</returns>
        /// <exception cref="WebhookVerificationException">The signature is missing, wrong or too old, or the body is not JSON.</exception>
        public static WebhookEvent Verify(byte[] payload, string? signature, string secret, TimeSpan? tolerance = null, DateTimeOffset? now = null) =>
            One(Verified(payload, signature, secret, tolerance, now));

        /// <summary>
        /// Checks a webhook request's signature and returns its events: every event of a batch (a webhook
        /// with batch delivery), or the one event of any other request.
        /// </summary>
        /// <param name="payload">The request body's bytes exactly as received.</param>
        /// <param name="signature">The Zap-Signature header's value.</param>
        /// <param name="secret">The webhook's signing secret (whsec_...).</param>
        /// <param name="tolerance">The oldest signature accepted (default five minutes).</param>
        /// <param name="now">The time to check against (default now).</param>
        /// <returns>The events.</returns>
        /// <exception cref="WebhookVerificationException">The signature is missing, wrong or too old, or the body is not events.</exception>
        public static IReadOnlyList<WebhookEvent> VerifyEvents(byte[] payload, string? signature, string secret, TimeSpan? tolerance = null, DateTimeOffset? now = null) =>
            All(Verified(payload, signature, secret, tolerance, now));

        /// <summary>
        /// Checks a webhook request's signature and returns its events: every event of a batch (a webhook
        /// with batch delivery), or the one event of any other request.
        /// </summary>
        /// <param name="payload">The request body exactly as received, before any JSON parsing.</param>
        /// <param name="signature">The Zap-Signature header's value.</param>
        /// <param name="secret">The webhook's signing secret (whsec_...).</param>
        /// <param name="tolerance">The oldest signature accepted (default five minutes).</param>
        /// <param name="now">The time to check against (default now).</param>
        /// <returns>The events.</returns>
        /// <exception cref="WebhookVerificationException">The signature is missing, wrong or too old, or the body is not events.</exception>
        public static IReadOnlyList<WebhookEvent> VerifyEvents(string payload, string? signature, string secret, TimeSpan? tolerance = null, DateTimeOffset? now = null) =>
            VerifyEvents(Encoding.UTF8.GetBytes(payload ?? throw new ArgumentNullException(nameof(payload))), signature, secret, tolerance, now);

        /// <summary>
        /// Checks a webhook request's signature, found in its headers, and returns its events: every event of
        /// a batch, or the one event of any other request.
        /// </summary>
        /// <param name="payload">The request body exactly as received, before any JSON parsing.</param>
        /// <param name="headers">The request's headers; the name is matched case-insensitively.</param>
        /// <param name="secret">The webhook's signing secret (whsec_...).</param>
        /// <param name="tolerance">The oldest signature accepted (default five minutes).</param>
        /// <param name="now">The time to check against (default now).</param>
        /// <returns>The events.</returns>
        /// <exception cref="WebhookVerificationException">The signature is missing, wrong or too old, or the body is not events.</exception>
        public static IReadOnlyList<WebhookEvent> VerifyEvents(string payload, IEnumerable<KeyValuePair<string, string>> headers, string secret, TimeSpan? tolerance = null, DateTimeOffset? now = null) =>
            VerifyEvents(payload, Find(headers), secret, tolerance, now);

        // checks the signature and returns the parsed body: an event, or an array of them for a batch
        private static JsonElement Verified(byte[] payload, string? signature, string secret, TimeSpan? tolerance, DateTimeOffset? now)
        {
            if (payload is null) throw new ArgumentNullException(nameof(payload));
            if (string.IsNullOrEmpty(signature)) throw new WebhookVerificationException("Missing Zap-Signature header");
            if (string.IsNullOrEmpty(secret)) throw new WebhookVerificationException("Missing webhook secret");
            var t = string.Empty;
            var v1 = new List<string>();
            foreach (var part in signature!.Split(','))
            {
                var i = part.IndexOf('=');
                if (i < 0) continue;
                var key = part.Substring(0, i).Trim();
                var value = part.Substring(i + 1).Trim();
                if (key == "t") t = value;
                else if (key == "v1") v1.Add(value);
            }
            // unix seconds: at most 12 digits
            if (t.Length > 12 || !long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp) || v1.Count == 0)
                throw new WebhookVerificationException("Malformed Zap-Signature header");
            var at = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
            if (Math.Abs((double)at - timestamp) > (tolerance ?? DefaultTolerance).TotalSeconds)
                throw new WebhookVerificationException("Webhook signature is too old (or from the future)");

            byte[] want;
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret)))
            {
                var prefix = Encoding.ASCII.GetBytes(t + ".");
                hmac.TransformBlock(prefix, 0, prefix.Length, null, 0);
                hmac.TransformFinalBlock(payload, 0, payload.Length);
                want = hmac.Hash!;
            }
            var ok = false;
            foreach (var hex in v1) ok |= TryHex(hex, out var got) && FixedTimeEquals(got, want);
            if (!ok) throw new WebhookVerificationException("Webhook signature does not match");

            try
            {
                using var doc = JsonDocument.Parse(payload);
                return doc.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                throw new WebhookVerificationException("Webhook body is not JSON", ex);
            }
        }

        private static WebhookEvent Event(JsonElement body) =>
            body.ValueKind == JsonValueKind.Object
                ? body.Deserialize<WebhookEvent>(ZapJson.Options)!
                : throw new WebhookVerificationException("Webhook body is not an event");

        private static WebhookEvent One(JsonElement body) =>
            body.ValueKind == JsonValueKind.Array
                ? throw new WebhookVerificationException("The body is a batch of events: use VerifyEvents")
                : Event(body);

        private static IReadOnlyList<WebhookEvent> All(JsonElement body)
        {
            if (body.ValueKind != JsonValueKind.Array) return new[] { Event(body) };
            var events = new List<WebhookEvent>();
            foreach (var e in body.EnumerateArray()) events.Add(Event(e));
            return events;
        }

        /// <summary>Checks a webhook request's signature, found in its headers, and returns its event.</summary>
        /// <param name="payload">The request body's bytes exactly as received.</param>
        /// <param name="headers">The request's headers; the name is matched case-insensitively.</param>
        /// <param name="secret">The webhook's signing secret (whsec_...).</param>
        /// <param name="tolerance">The oldest signature accepted (default five minutes).</param>
        /// <param name="now">The time to check against (default now).</param>
        /// <returns>The event.</returns>
        /// <exception cref="WebhookVerificationException">The signature is missing, wrong or too old, or the body is not JSON.</exception>
        public static WebhookEvent Verify(byte[] payload, IEnumerable<KeyValuePair<string, string>> headers, string secret, TimeSpan? tolerance = null, DateTimeOffset? now = null) =>
            Verify(payload, Find(headers), secret, tolerance, now);

        private static string? Find(IEnumerable<KeyValuePair<string, string>> headers)
        {
            if (headers is null) throw new ArgumentNullException(nameof(headers));
            foreach (var pair in headers)
            {
                if (string.Equals(pair.Key, SignatureHeader, StringComparison.OrdinalIgnoreCase)) return pair.Value;
            }
            return null;
        }

        private static bool TryHex(string hex, out byte[] bytes)
        {
            bytes = new byte[32];
            if (hex.Length != 64) return false;
            for (var i = 0; i < 32; i++)
            {
                var hi = Nibble(hex[2 * i]);
                var lo = Nibble(hex[2 * i + 1]);
                if ((hi | lo) < 0) return false;
                bytes[i] = (byte)((hi << 4) | lo);
            }
            return true;
        }

        private static int Nibble(char c) =>
            c >= '0' && c <= '9' ? c - '0' :
            c >= 'a' && c <= 'f' ? c - 'a' + 10 :
            c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;

        // constant time: every byte is compared whatever the earlier ones were
        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            var diff = 0;
            for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }

    /// <summary>A webhook request's body.</summary>
    public sealed class WebhookEvent : ZapObject
    {
        /// <summary>evt_...; absent on email.received, thread.created and thread.replied (see the Zap-Event-Id header).</summary>
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        /// <summary>The event type, such as email.delivered; see <see cref="WebhookEventType"/>.</summary>
        [JsonPropertyName("type")]
        public string? Type { get; init; }

        /// <summary>When the event happened.</summary>
        [JsonPropertyName("created_at")]
        public DateTimeOffset? CreatedAt { get; init; }

        /// <summary>The event's data, as raw JSON; <see cref="GetData{T}"/> decodes it.</summary>
        [JsonPropertyName("data")]
        public JsonElement? Data { get; init; }

        /// <summary>The event's data decoded into <typeparamref name="T"/>, or default when there is none.</summary>
        /// <typeparam name="T">A type for the data, such as a model of your own.</typeparam>
        /// <returns>The data.</returns>
        public T? GetData<T>() => Data is { } data ? data.Deserialize<T>(ZapJson.Options) : default;
    }
}
