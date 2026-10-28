using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace ZapSMTP.Tests
{
    public sealed class WebhookTests
    {
        private static readonly JsonElement[] Vectors = Paths.ReadJson("spec", "webhook-vectors.json").GetProperty("vectors").EnumerateArray().ToArray();

        public static IEnumerable<object[]> VectorNames() => Vectors.Select(v => new object[] { v.GetProperty("name").GetString()! });

        private static JsonElement Vector(string name) => Vectors.Single(v => v.GetProperty("name").GetString() == name);

        private static DateTimeOffset Now(JsonElement v) => DateTimeOffset.FromUnixTimeSeconds(v.GetProperty("now").GetInt64());

        private static string Sign(string secret, long t, string payload)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            return $"t={t},v1={BitConverter.ToString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{t}.{payload}"))).Replace("-", string.Empty).ToLowerInvariant()}";
        }

        [Theory]
        [MemberData(nameof(VectorNames))]
        public void Vector_passes_or_fails_as_it_says(string name)
        {
            var v = Vector(name);
            var payload = v.GetProperty("payload").GetString()!;
            var headers = new Dictionary<string, string> { ["Zap-Signature"] = v.GetProperty("header").GetString()! };
            var secret = v.GetProperty("secret").GetString()!;
            var tolerance = TimeSpan.FromSeconds(v.GetProperty("tolerance").GetDouble());
            WebhookEvent One() => Webhooks.Verify(payload, headers, secret, tolerance, Now(v));
            IReadOnlyList<WebhookEvent> All() => Webhooks.VerifyEvents(payload, headers, secret, tolerance, Now(v));
            var want = v.GetProperty("valid").GetBoolean() ? JsonDocument.Parse(payload).RootElement : default;
            if (!v.GetProperty("valid").GetBoolean())
            {
                Assert.Throws<WebhookVerificationException>(One);
                Assert.Throws<WebhookVerificationException>(All);
            }
            else if (v.TryGetProperty("batch", out var batch) && batch.GetBoolean())
            {
                Assert.Matches("batch of events: use VerifyEvents", Assert.Throws<WebhookVerificationException>(One).Message);
                AssertJson.Same(want, JsonSerializer.SerializeToElement(All(), Internal.ZapJson.Options));
            }
            else
            {
                AssertJson.Same(want, JsonSerializer.SerializeToElement(One(), Internal.ZapJson.Options));
                AssertJson.Same(want, JsonSerializer.SerializeToElement(Assert.Single(All()), Internal.ZapJson.Options));
            }
        }

        [Fact]
        public void Takes_headers_a_header_value_bytes_and_the_client_method()
        {
            var v = Vectors[0];
            var payload = v.GetProperty("payload").GetString()!;
            var header = v.GetProperty("header").GetString()!;
            var secret = v.GetProperty("secret").GetString()!;
            var now = Now(v);
            var lower = new[] { new KeyValuePair<string, string>("x-other", "1"), new KeyValuePair<string, string>("zap-signature", header) };
            var evt = Webhooks.Verify(Encoding.UTF8.GetBytes(payload), lower, secret, now: now);
            Assert.Equal("evt_1", evt.Id);
            Assert.Equal("email.delivered", evt.Type);
            Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), evt.CreatedAt);
            Assert.Equal("jane@example.org", evt.Data!.Value.GetProperty("to").GetString());
            Assert.Equal("Привет ✓", evt.GetData<Dictionary<string, string>>()!["subject"]);
            Assert.Equal("email.delivered", Webhooks.Verify(payload, header, secret, now: now).Type);
            Assert.Equal("evt_1", Webhooks.Verify(Encoding.UTF8.GetBytes(payload), header, secret, now: now).Id);
            var zap = new ZapSMTPClient("k");
            Assert.Equal("evt_1", zap.Webhooks.Verify(payload, header, secret, now: now).Id);
            Assert.Equal("evt_1", zap.Webhooks.Verify(payload, lower, secret, now: now).Id);
            Assert.Null(new WebhookEvent().GetData<object>());
        }

        [Fact]
        public void Refuses_a_signed_body_that_is_not_an_event()
        {
            const long t = 1700000000;
            var now = DateTimeOffset.FromUnixTimeSeconds(t);
            foreach (var body in new[] { "42", "null", "[1]", "[[]]" })
            {
                var header = Sign("whsec_x", t, body);
                Assert.Matches("not an event", Assert.Throws<WebhookVerificationException>(() => Webhooks.VerifyEvents(body, header, "whsec_x", now: now)).Message);
                Assert.Matches("not an event", Assert.Throws<WebhookVerificationException>(() => Webhooks.VerifyEvents(Encoding.UTF8.GetBytes(body), header, "whsec_x", now: now)).Message);
                if (!body.StartsWith("[", StringComparison.Ordinal))
                    Assert.Matches("not an event", Assert.Throws<WebhookVerificationException>(() => Webhooks.Verify(body, header, "whsec_x", now: now)).Message);
            }
            var ok = Sign("whsec_x", t, "{}");
            var zap = new ZapSMTPClient("k");
            Assert.Single(zap.Webhooks.VerifyEvents("{}", ok, "whsec_x", now: now));
            Assert.Single(zap.Webhooks.VerifyEvents("{}", new Dictionary<string, string> { ["Zap-Signature"] = ok }, "whsec_x", now: now));
            Assert.Throws<ArgumentNullException>(() => Webhooks.VerifyEvents((string)null!, ok, "s"));
        }

        [Fact]
        public void Checks_against_the_clock_by_default()
        {
            var t = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var payload = "{\"id\":\"evt_2\",\"type\":\"email.sent\"}";
            Assert.Equal("evt_2", Webhooks.Verify(payload, Sign("whsec_x", t - 10, payload), "whsec_x").Id);
            var old = Assert.Throws<WebhookVerificationException>(() => Webhooks.Verify(payload, Sign("whsec_x", t - 301, payload), "whsec_x"));
            Assert.Matches("too old", old.Message);
            Assert.Equal("evt_2", Webhooks.Verify(payload, Sign("whsec_x", t - 500, payload), "whsec_x", TimeSpan.FromMinutes(10)).Id);
        }

        [Fact]
        public void Refuses_a_missing_header_or_secret_and_a_body_that_is_not_JSON()
        {
            Assert.Matches("Missing Zap-Signature", Assert.Throws<WebhookVerificationException>(() => Webhooks.Verify("{}", new Dictionary<string, string>(), "s")).Message);
            Assert.Matches("Missing Zap-Signature", Assert.Throws<WebhookVerificationException>(() => Webhooks.Verify("{}", (string?)null, "s")).Message);
            Assert.Matches("Missing webhook secret", Assert.Throws<WebhookVerificationException>(() => Webhooks.Verify("{}", "t=1,v1=00", string.Empty)).Message);
            Assert.Matches("Malformed", Assert.Throws<WebhookVerificationException>(() => Webhooks.Verify("{}", "t=x,v1=00,junk", "s")).Message);
            Assert.Matches("Malformed", Assert.Throws<WebhookVerificationException>(() => Webhooks.Verify("{}", "t=99999999999999999999,v1=00", "s")).Message);
            Assert.Matches("Malformed", Assert.Throws<WebhookVerificationException>(() => Webhooks.Verify("{}", "t=1234567890123,v1=00", "s")).Message);
            const long t = 1700000000;
            var now = DateTimeOffset.FromUnixTimeSeconds(t);
            var notJson = Assert.Throws<WebhookVerificationException>(() => Webhooks.Verify("not json", Sign("whsec_x", t, "not json"), "whsec_x", now: now));
            Assert.Matches("not JSON", notJson.Message);
            Assert.IsAssignableFrom<JsonException>(notJson.InnerException);
                        var upper = Sign("whsec_x", t, "{}").ToUpperInvariant().Replace("T=", "t=").Replace("V1=", "v1=");
            Assert.NotNull(Webhooks.Verify("{}", upper, "whsec_x", now: now));
            Assert.Matches("does not match", Assert.Throws<WebhookVerificationException>(() => Webhooks.Verify("{}", $"t={t},v1={new string('g', 64)}", "whsec_x", now: now)).Message);
            Assert.Throws<ArgumentNullException>(() => Webhooks.Verify((string)null!, "t=1", "s"));
            Assert.Throws<ArgumentNullException>(() => Webhooks.Verify((byte[])null!, "t=1", "s"));
            Assert.Throws<ArgumentNullException>(() => Webhooks.Verify("{}", (IEnumerable<KeyValuePair<string, string>>)null!, "s"));
        }
    }

    public sealed class AttachmentTests
    {
        [Fact]
        public void Encodes_bytes_and_text_and_guesses_the_content_type()
        {
            var a = OutgoingAttachment.FromBytes("a.pdf", new byte[] { 1, 2 });
            Assert.Equal("a.pdf", a.Filename);
            Assert.Equal("AQI=", a.Content);
            Assert.Equal("application/pdf", a.ContentType);
            Assert.Null(a.ContentId);
            var n = OutgoingAttachment.FromText("notes", "hi", "text/plain", "n1");
            Assert.Equal("aGk=", n.Content);
            Assert.Equal("text/plain", n.ContentType);
            Assert.Equal("n1", n.ContentId);
            Assert.Null(OutgoingAttachment.FromBytes("x.unknown", new byte[1]).ContentType);
            Assert.Equal("{\"filename\":\"a.pdf\",\"content\":\"AQI=\",\"content_type\":\"application/pdf\"}",
                JsonSerializer.Serialize(a, Internal.ZapJson.Options));
            Assert.Throws<ArgumentNullException>(() => OutgoingAttachment.FromBytes(null!, new byte[1]));
            Assert.Throws<ArgumentNullException>(() => OutgoingAttachment.FromBytes("a", null!));
            Assert.Throws<ArgumentNullException>(() => OutgoingAttachment.FromText("a", null!));
        }

        [Fact]
        public async Task Reads_a_file()
        {
            var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "zap-" + Guid.NewGuid().ToString("N")));
            try
            {
                var path = Path.Combine(dir.FullName, "Report.PNG");
                File.WriteAllBytes(path, new byte[] { 137, 80 });
                var a = await OutgoingAttachment.FromFileAsync(path);
                Assert.Equal("Report.PNG", a.Filename);
                Assert.Equal("iVA=", a.Content);
                Assert.Equal("image/png", a.ContentType);
                Assert.Equal("r.png", (await OutgoingAttachment.FromFileAsync(path, "r.png")).Filename);
                await Assert.ThrowsAsync<ArgumentNullException>(() => OutgoingAttachment.FromFileAsync(null!));
                await Assert.ThrowsAsync<FileNotFoundException>(() => OutgoingAttachment.FromFileAsync(Path.Combine(dir.FullName, "missing.pdf")));
            }
            finally
            {
                dir.Delete(true);
            }
        }
    }
}
