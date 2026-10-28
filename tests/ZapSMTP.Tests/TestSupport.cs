using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ZapSMTP.Tests
{
    /// <summary>A request the fake handler received.</summary>
    internal sealed class Call
    {
        public Call(HttpRequestMessage request, string? body)
        {
            Method = request.Method.Method;
            Url = request.RequestUri!.AbsoluteUri;
            Body = body;
            ContentType = request.Content?.Headers.ContentType?.ToString();
            foreach (var h in request.Headers) Headers[h.Key] = string.Join(", ", h.Value);
            if (request.Headers.UserAgent.Count > 0) Headers["User-Agent"] = request.Headers.UserAgent.ToString(); // as sent: space-separated
            if (request.Content is not null) foreach (var h in request.Content.Headers) Headers[h.Key] = string.Join(", ", h.Value);
        }

        public string Method { get; }
        public string Url { get; }
        public string? Body { get; }
        public string? ContentType { get; }
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
        public string Query => new Uri(Url).Query;
        public JsonElement Json => JsonDocument.Parse(Body!).RootElement;
    }

    /// <summary>Answers from a list of responses (the last one repeats) and records each request.</summary>
    internal sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _answers = new();
        private Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? _last;

        public List<Call> Calls { get; } = new();

        public FakeHandler Answer(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer)
        {
            _answers.Enqueue(answer);
            return this;
        }

        public FakeHandler Answer(Func<HttpResponseMessage> response) => Answer((_, _) => Task.FromResult(response()));

        public FakeHandler Throw(Exception error) => Answer((_, _) => Task.FromException<HttpResponseMessage>(error));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            Calls.Add(new Call(request, body));
            var answer = _answers.Count > 0 ? (_last = _answers.Dequeue()) : _last ?? throw new InvalidOperationException("no answer");
            return await answer(request, cancellationToken);
        }
    }

    internal static class Http
    {
        public static HttpResponseMessage Json(int status, object? body, params (string Name, string Value)[] headers)
        {
            var res = new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(body is string s ? s : JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            res.Headers.TryAddWithoutValidation("X-Request-Id", "req_1");
            foreach (var (name, value) in headers) res.Headers.TryAddWithoutValidation(name, value);
            return res;
        }

        public static HttpResponseMessage Text(int status, string text) =>
            new((HttpStatusCode)status) { Content = new StringContent(text) };

        public static HttpResponseMessage NoContent() => new(HttpStatusCode.NoContent) { Content = new ByteArrayContent(Array.Empty<byte>()) };

        public static HttpResponseMessage Error(int status, string code, params (string Name, string Value)[] headers) =>
            Json(status, new
            {
                statusCode = status,
                name = code,
                message = $"{code} happened",
                error = new { code, message = $"{code} happened", type = "invalid_request_error" },
            }, headers);

        public static HttpResponseMessage List(IEnumerable<object> data, bool hasMore = true, object? extra = null)
        {
            var body = new Dictionary<string, object?> { ["object"] = "list", ["has_more"] = hasMore, ["data"] = data.ToArray() };
            if (extra is not null) foreach (var p in extra.GetType().GetProperties()) body[p.Name] = p.GetValue(extra);
            return Json(200, body);
        }
    }

    internal static class Clients
    {
        /// <summary>A client on the fake handler that records its waits instead of waiting.</summary>
        public static ZapSMTPClient Make(FakeHandler handler, List<TimeSpan>? delays = null, Action<ZapSMTPClientOptions>? configure = null)
        {
            var options = new ZapSMTPClientOptions { BaseUrl = "https://api.test/v1/", HttpClient = new HttpClient(handler) };
            configure?.Invoke(options);
            var zap = new ZapSMTPClient("zap_test_key", options);
            zap.Core.Delay = (d, _) =>
            {
                delays?.Add(d);
                return Task.CompletedTask;
            };
            return zap;
        }
    }

    internal static class Paths
    {
        /// <summary>The sdks directory (spec, conformance), found above the test binaries.</summary>
        public static string Sdks { get; } = Find();

        private static string Find()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "conformance", "cases.json"))) return dir.FullName;
            }
            throw new InvalidOperationException("sdks/conformance/cases.json not found above " + AppContext.BaseDirectory);
        }

        public static JsonElement ReadJson(params string[] parts) =>
            JsonDocument.Parse(File.ReadAllText(Path.Combine(new[] { Sdks }.Concat(parts).ToArray()))).RootElement;
    }

    /// <summary>Tests that change environment variables run alone.</summary>
    [CollectionDefinition("env", DisableParallelization = true)]
    public sealed class EnvCollection
    {
    }

    internal static class AssertJson
    {
        /// <summary>
        /// Whether a decoded value, written back as JSON, says what the API sent: times compare as
        /// instants, numbers by value, and a missing field equals null.
        /// </summary>
        public static void Same(JsonElement want, JsonElement? got, string path = "$")
        {
            if (want.ValueKind == JsonValueKind.Null)
            {
                Assert.True(got is null || got.Value.ValueKind == JsonValueKind.Null, $"{path}: want null, got {got}");
                return;
            }
            Assert.True(got is not null, $"{path}: missing, want {want.GetRawText()}");
            var g = got!.Value;
            switch (want.ValueKind)
            {
                case JsonValueKind.Object:
                    Assert.Equal(JsonValueKind.Object, g.ValueKind);
                    foreach (var p in want.EnumerateObject())
                        Same(p.Value, g.TryGetProperty(p.Name, out var v) ? v : null, $"{path}.{p.Name}");
                    foreach (var p in g.EnumerateObject())
                        Assert.True(want.TryGetProperty(p.Name, out _), $"{path}.{p.Name}: not in the response");
                    break;
                case JsonValueKind.Array:
                    Assert.Equal(JsonValueKind.Array, g.ValueKind);
                    Assert.Equal(want.GetArrayLength(), g.GetArrayLength());
                    for (var i = 0; i < want.GetArrayLength(); i++) Same(want[i], g[i], $"{path}[{i}]");
                    break;
                case JsonValueKind.Number:
                    Assert.True(g.ValueKind == JsonValueKind.Number, $"{path}: want {want}, got {g.GetRawText()}");
                    Assert.Equal(want.GetDouble(), g.GetDouble());
                    break;
                case JsonValueKind.String:
                    Assert.True(g.ValueKind == JsonValueKind.String, $"{path}: want {want.GetRawText()}, got {g.GetRawText()}");
                    if (want.GetString() == g.GetString()) break;
                    Assert.True(DateTimeOffset.TryParse(want.GetString(), out var a) && DateTimeOffset.TryParse(g.GetString(), out var b) && a == b,
                        $"{path}: want {want.GetRawText()}, got {g.GetRawText()}");
                    break;
                default:
                    Assert.Equal(want.ValueKind, g.ValueKind);
                    break;
            }
        }

        /// <summary>Fails when a decoded model kept a field in AdditionalProperties: every field must be typed.</summary>
        public static void AllTyped(object? value, string path = "$")
        {
            switch (value)
            {
                case null:
                case string:
                case JsonElement:
                    return;
                case ZapObject o:
                    Assert.True(o.AdditionalProperties is null || o.AdditionalProperties.Count == 0,
                        $"{path}: untyped fields {string.Join(", ", o.AdditionalProperties?.Keys ?? Array.Empty<string>())}");
                    break;
                case ZapParams p:
                    Assert.True(p.AdditionalProperties is null || p.AdditionalProperties.Count == 0,
                        $"{path}: untyped fields {string.Join(", ", p.AdditionalProperties?.Keys ?? Array.Empty<string>())}");
                    break;
                case System.Collections.IDictionary d:
                    foreach (System.Collections.DictionaryEntry e in d) AllTyped(e.Value, $"{path}.{e.Key}");
                    return;
                case System.Collections.IEnumerable list:
                    var i = 0;
                    foreach (var item in list) AllTyped(item, $"{path}[{i++}]");
                    return;
                default:
                    return;
            }
            foreach (var prop in value.GetType().GetProperties())
            {
                if (prop.Name == "AdditionalProperties" || prop.GetIndexParameters().Length > 0) continue;
                AllTyped(prop.GetValue(value), $"{path}.{prop.Name}");
            }
        }
    }
}
