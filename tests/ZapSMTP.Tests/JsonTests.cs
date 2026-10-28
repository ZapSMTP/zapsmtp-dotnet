using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using ZapSMTP.Internal;

namespace ZapSMTP.Tests
{
    public sealed class JsonTests
    {
        private static T Decode<T>(string json) where T : class, new() => ZapJson.Decode<T>(json);

        [Fact]
        public void Decodes_every_type()
        {
            var e = Decode<Email>("{\"id\":\"e1\",\"created_at\":\"2026-01-02T03:04:05Z\",\"test\":true,\"to\":[\"a@x.test\"]," +
                "\"metadata\":{\"k\":\"v\"},\"events\":[{\"attempts\":2,\"type\":\"delivered\",\"time\":\"2026-01-02T03:04:05.5+02:00\"}],\"brand_new\":{\"x\":1}}");
            Assert.Equal("e1", e.Id);
            Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), e.CreatedAt);
            Assert.True(e.Test);
            Assert.Equal(new[] { "a@x.test" }, e.To);
            Assert.Equal("v", e.Metadata!["k"]);
            Assert.Equal(2, e.Events![0].Attempts);
            Assert.Equal(TimeSpan.FromHours(2), e.Events[0].Time!.Value.Offset);
            Assert.Equal(1, e.AdditionalProperties!["brand_new"].GetProperty("x").GetInt32());
            var s = Decode<SpamCheck>("{\"score\":1.5,\"threshold\":5}");
            Assert.Equal(1.5, s.Score);
            Assert.Equal(5, s.Threshold);
            var t = Decode<Template>("{\"design\":{\"rows\":[]},\"variables\":{\"a\":\"b\"},\"published\":{\"subject\":\"s\"}}");
            Assert.Equal(JsonValueKind.Array, t.Design!["rows"].ValueKind);
            Assert.Equal("s", t.Published!.Subject);
            var r = Decode<RenderedTemplate>("{\"design\":null}");
            Assert.Null(r.Design);
        }

        [Fact]
        public void Never_fails_on_surprises()
        {
            var e = Decode<Email>("{\"id\":7,\"created_at\":\"not a time\",\"test\":\"true\",\"to\":\"a@x.test\",\"metadata\":[1]," +
                "\"events\":[\"junk\",null,{\"attempts\":\"3\",\"response_code\":1.5,\"time\":5}],\"subject\":{\"a\":1},\"status\":[1,2],\"from\":true,\"error\":null}");
            Assert.Equal("7", e.Id);
            Assert.Null(e.CreatedAt);
            Assert.True(e.Test);
            Assert.Null(e.To);
            Assert.Null(e.Metadata);
            var ev = Assert.Single(e.Events!);
            Assert.Equal(3, ev.Attempts);
            Assert.Null(ev.ResponseCode);
            Assert.Null(ev.Time);
            Assert.Null(e.Subject);
            Assert.Null(e.Status);
            Assert.Equal("true", e.From);
            Assert.Null(e.Error);

            var odd = Decode<EmailEvent>("{\"attempts\":true,\"response_code\":\"x\",\"recipient\":false}");
            Assert.Null(odd.Attempts);
            Assert.Null(odd.ResponseCode);
            Assert.Equal("false", odd.Recipient);
            var big = Decode<EmailEvent>("{\"attempts\":99999999999999999999}");
            Assert.Null(big.Attempts);
            var spam = Decode<SpamCheck>("{\"score\":\"2.5\",\"threshold\":\"x\",\"dkim_signed\":\"no\",\"engines\":{}}");
            Assert.Equal(2.5, spam.Score);
            Assert.Null(spam.Threshold);
            Assert.Null(spam.DkimSigned);
            Assert.Null(spam.Engines);
            var spam2 = Decode<SpamCheck>("{\"score\":[],\"dkim_signed\":1}");
            Assert.Null(spam2.Score);
            Assert.Null(spam2.DkimSigned);
            var nested = Decode<Template>("{\"published\":\"yes\",\"variables\":{\"a\":1,\"b\":null,\"c\":{}}}");
            Assert.Null(nested.Published);
            Assert.Equal("1", nested.Variables!["a"]);
            Assert.False(nested.Variables.ContainsKey("b"));
            Assert.False(nested.Variables.ContainsKey("c"));
            var hook = Decode<Webhook>("{\"filter\":{\"actions\":[\"a\",5]}}");
            Assert.Equal(new[] { "a", "5" }, hook.Filter!.Actions);
        }

        [Fact]
        public void An_unknown_key_never_overwrites_a_known_property()
        {
            var e = Decode<Email>("{\"id\":\"e1\",\"Id\":\"x\",\"ID\":\"y\",\"AdditionalProperties\":{\"id\":\"z\"},\"additional_properties\":1}");
            Assert.Equal("e1", e.Id);
            Assert.Equal(new[] { "Id", "ID", "AdditionalProperties", "additional_properties" }, e.AdditionalProperties!.Keys);
            var p = Decode<EmailEvent>("{\"Attempts\":9,\"attempts\":2}");
            Assert.Equal(2, p.Attempts);
        }

        [Fact]
        public void Writes_models_back_as_the_API_writes_them()
        {
            var json = JsonSerializer.Serialize(new EmailEvent { Attempts = 2, Time = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), ResponseCode = null },
                ZapJson.Options);
            Assert.Equal("{\"attempts\":2,\"time\":\"2026-01-02T03:04:05+00:00\"}", json);
            Assert.Equal("{\"dkim_signed\":false,\"score\":1.5}", JsonSerializer.Serialize(new SpamCheck { DkimSigned = false, Score = 1.5 }, ZapJson.Options));
            Assert.Equal("{\"published\":{\"subject\":\"s\"},\"variables\":{\"a\":\"b\"}}",
                JsonSerializer.Serialize(new Template { Published = new TemplatePublished { Subject = "s" }, Variables = new Dictionary<string, string> { ["a"] = "b" } }, ZapJson.Options));
        }

        [Fact]
        public void Reads_and_writes_optional_fields()
        {
            var p = JsonSerializer.Deserialize<ApiKeysUpdateParams>("{\"domain_id\":null,\"stream\":\"broadcast\"}", ZapJson.Options)!;
            Assert.True(p.DomainId.HasValue);
            Assert.Null(p.DomainId.Value);
            Assert.Equal("broadcast", p.Stream.Value);
            Assert.Equal("{\"domain_id\":null,\"stream\":\"broadcast\"}", JsonSerializer.Serialize(p, ZapJson.Options));
        }

        [Fact]
        public void Converts_only_Optional()
        {
            var f = new OptionalConverterFactory();
            Assert.True(f.CanConvert(typeof(Optional<string?>)));
            Assert.False(f.CanConvert(typeof(string)));
            Assert.False(f.CanConvert(typeof(List<string>)));
        }

        [Fact]
        public void Parses_only_JSON()
        {
            Assert.Null(ZapJson.TryParse(string.Empty));
            Assert.Null(ZapJson.TryParse("{"));
            Assert.Equal(JsonValueKind.Number, ZapJson.TryParse("1")!.Value.ValueKind);
            Assert.Null(ZapJson.String(ZapJson.TryParse("[]"), "x"));
            Assert.Null(ZapJson.Object(ZapJson.TryParse("{\"x\":1}"), "x"));
        }

        [Fact]
        public async Task Answers_decode_into_typed_models_through_the_client()
        {
            var h = new FakeHandler().Answer(() => Http.Json(200, new { id = "w1", filter = new { actions = new[] { "buy" } }, events = new[] { WebhookEventType.EmailBounced } }));
            var w = await Clients.Make(h).Webhooks.GetAsync("w1");
            Assert.Equal("buy", w.Filter!.Actions![0]);
            Assert.Equal("email.bounced", w.Events![0]);
        }
    }
}
