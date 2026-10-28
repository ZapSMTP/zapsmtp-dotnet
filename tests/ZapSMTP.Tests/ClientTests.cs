using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ZapSMTP.Internal;

namespace ZapSMTP.Tests
{
    [Collection("env")]
    public sealed class EnvironmentTests : IDisposable
    {
        private readonly string? _key = Environment.GetEnvironmentVariable("ZAP_API_KEY");
        private readonly string? _url = Environment.GetEnvironmentVariable("ZAP_BASE_URL");

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("ZAP_API_KEY", _key);
            Environment.SetEnvironmentVariable("ZAP_BASE_URL", _url);
        }

        [Fact]
        public async Task Reads_the_key_and_base_URL_from_the_environment()
        {
            Environment.SetEnvironmentVariable("ZAP_API_KEY", "zap_env");
            Environment.SetEnvironmentVariable("ZAP_BASE_URL", "https://env.test/v1");
            var h = new FakeHandler().Answer(() => Http.Json(200, new { id = "e1" }));
            using var zap = new ZapSMTPClient(options: new ZapSMTPClientOptions { HttpClient = new HttpClient(h) });
            Assert.Equal("zap_env", zap.Core.ApiKey);
            await zap.Emails.GetAsync("e1");
            Assert.Equal("https://env.test/v1/emails/e1", h.Calls[0].Url);
            Assert.Equal("Bearer zap_env", h.Calls[0].Header("Authorization"));
        }

        [Fact]
        public void Prefers_the_argument_then_the_options()
        {
            Environment.SetEnvironmentVariable("ZAP_API_KEY", "zap_env");
            Assert.Equal("zap_arg", new ZapSMTPClient("zap_arg", new ZapSMTPClientOptions { ApiKey = "zap_opt" }).Core.ApiKey);
            Assert.Equal("zap_opt", new ZapSMTPClient(null, new ZapSMTPClientOptions { ApiKey = "zap_opt" }).Core.ApiKey);
            Assert.Equal("zap_env", new ZapSMTPClient().Core.ApiKey);
        }

        [Fact]
        public void Defaults_to_the_production_API()
        {
            Environment.SetEnvironmentVariable("ZAP_BASE_URL", null);
            using var zap = new ZapSMTPClient("k");
            Assert.Equal("https://api.zapsmtp.com/v1", zap.BaseUrl);
            Assert.Equal(TimeSpan.FromSeconds(60), zap.Timeout);
            Assert.Equal(2, zap.MaxRetries);
            Environment.SetEnvironmentVariable("ZAP_BASE_URL", string.Empty);
            Assert.Equal(ZapSMTPClientOptions.DefaultBaseUrl, new ZapSMTPClient("k", new ZapSMTPClientOptions { BaseUrl = string.Empty }).BaseUrl);
        }

        [Fact]
        public void Needs_a_key()
        {
            Environment.SetEnvironmentVariable("ZAP_API_KEY", null);
            var e = Assert.Throws<ZapSMTPException>(() => new ZapSMTPClient());
            Assert.Contains("Missing API key", e.Message);
            Assert.Throws<ZapSMTPException>(() => new ZapSMTPClient(string.Empty));
        }
    }

    public sealed class ClientTests
    {
        [Fact]
        public void Checks_the_timeout_and_retries()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ZapSMTPClient("k", new ZapSMTPClientOptions { Timeout = TimeSpan.Zero }));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ZapSMTPClient("k", new ZapSMTPClientOptions { MaxRetries = -1 }));
            using var infinite = new ZapSMTPClient("k", new ZapSMTPClientOptions { Timeout = Timeout.InfiniteTimeSpan, MaxRetries = 0 });
            Assert.Equal(Timeout.InfiniteTimeSpan, infinite.Timeout);
        }

        [Fact]
        public async Task Sends_the_headers_every_request_carries()
        {
            var h = new FakeHandler().Answer(() => Http.Json(200, new { id = "x" }));
            var zap = Clients.Make(h, configure: o => o.DefaultHeaders = new Dictionary<string, string> { ["X-Team"] = "mail" });
            await zap.Emails.GetAsync("a/b c", new RequestOptions { Headers = new Dictionary<string, string> { ["X-Extra"] = "1" } });
            var call = h.Calls[0];
            Assert.Equal("https://api.test/v1/emails/a%2Fb%20c", call.Url);
            Assert.Equal("GET", call.Method);
            Assert.Matches(new Regex($@"^zapsmtp-dotnet/{Regex.Escape(ZapSMTPClient.Version)} \(dotnet/\d+\.\d+"), call.Header("User-Agent"));
            Assert.Equal("application/json", call.Header("Accept"));
            Assert.Equal("Bearer zap_test_key", call.Header("Authorization"));
            Assert.Equal("mail", call.Header("X-Team"));
            Assert.Equal("1", call.Header("X-Extra"));
            Assert.Null(call.Header("Content-Type"));
            Assert.Null(call.Header("Idempotency-Key"));
            Assert.Null(call.Body);
        }

        [Fact]
        public void Version_matches_the_assembly()
        {
            var info = typeof(ZapSMTPClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
            Assert.StartsWith(ZapSMTPClient.Version, info);
        }

        [Fact]
        public async Task Serializes_query_parameters_arrays_repeat_maps_become_key_name()
        {
            var h = new FakeHandler().Answer(() => Http.List(Array.Empty<object>(), false));
            var zap = Clients.Make(h);
            await zap.Emails.ListAsync(new EmailsListParams { Limit = 5, Q = null, Metadata = new Dictionary<string, string> { ["order_id"] = "A-1", ["a b"] = "c" } });
            Assert.Equal("https://api.test/v1/emails?limit=5&metadata%5Border_id%5D=A-1&metadata%5Ba+b%5D=c", h.Calls[0].Url);
            await zap.Threads.ListAsync(new ThreadsListParams { Unread = true, AwaitingReply = false });
            Assert.Equal("?awaiting_reply=false&unread=true", h.Calls[1].Query);
            await zap.Emails.ListAsync();
            Assert.Equal("https://api.test/v1/emails", h.Calls[2].Url);

            var q = new QueryString();
            q.Add("tag", new[] { "a", "b" });
            q.Add("none", (IReadOnlyList<string>?)null);
            q.Add("map", (IReadOnlyDictionary<string, string>?)null);
            q.Add("n", (int?)null);
            q.Add("b", (bool?)null);
            Assert.Equal("tag=a&tag=b", q.ToString());
        }

        [Fact]
        public async Task Sends_parameters_the_SDK_does_not_know()
        {
            var h = new FakeHandler().Answer(() => Http.List(Array.Empty<object>(), false));
            var zap = Clients.Make(h);
            await zap.Logs.ListAsync(new LogsListParams
            {
                AdditionalProperties = new Dictionary<string, object?>
                {
                    ["s"] = "x", ["t"] = true, ["f"] = false, ["n"] = 1.5, ["skip"] = null, ["u"] = new Uri("https://a.test/"), ["o"] = new Sku(),
                    ["js"] = System.Text.Json.JsonDocument.Parse("\"j\"").RootElement, ["jn"] = System.Text.Json.JsonDocument.Parse("7").RootElement,
                },
            });
            Assert.Equal("?s=x&t=true&f=false&n=1.5&u=https%3A%2F%2Fa.test%2F&o=sku-1&js=j&jn=7", h.Calls[0].Query);
            await zap.Domains.CreateAsync(new DomainsCreateParams { Name = "a.test", AdditionalProperties = new Dictionary<string, object?> { ["beta"] = true } });
            Assert.Equal("{\"name\":\"a.test\",\"beta\":true}", h.Calls[1].Body);
        }

        [Fact]
        public async Task Sends_JSON_bodies_and_an_Idempotency_Key_on_sends()
        {
            var h = new FakeHandler().Answer(() => Http.Json(200, new { id = "e1" }));
            var zap = Clients.Make(h);
            var sent = await zap.Emails.SendAsync(new SendEmailParams { From = "a@x.test", To = new[] { "b@y.test" }, Subject = "Hi", Text = "Hello" });
            Assert.Equal("e1", sent.Id);
            var call = h.Calls[0];
            Assert.Equal("POST", call.Method);
            Assert.Equal("application/json", call.ContentType);
            Assert.Matches("^[0-9a-f-]{36}$", call.Header("Idempotency-Key"));
            Assert.Equal("{\"from\":\"a@x.test\",\"to\":[\"b@y.test\"],\"subject\":\"Hi\",\"text\":\"Hello\"}", call.Body);
            await zap.Emails.SendAsync(new SendEmailParams { From = "a", To = new[] { "b" }, Subject = "s" }, new RequestOptions { IdempotencyKey = "order-1" });
            Assert.Equal("order-1", h.Calls[1].Header("Idempotency-Key"));
            await Assert.ThrowsAsync<ArgumentNullException>(() => zap.Emails.SendAsync(null!));
            await Assert.ThrowsAsync<ValidationException>(() => zap.Emails.GetAsync(null!));
        }

        [Fact]
        public async Task Refuses_an_id_that_cannot_be_a_path_segment_without_a_request()
        {
            var h = new FakeHandler().Answer(Http.NoContent);
            var zap = Clients.Make(h);
            foreach (var id in new[] { "..", ".", string.Empty })
            {
                var e = await Assert.ThrowsAsync<ValidationException>(() => zap.Domains.DeleteAsync(id));
                Assert.Equal("invalid_parameter", e.Code);
                Assert.Equal(0, e.StatusCode);
            }
            Assert.Equal("id must not be \"..\"", (await Assert.ThrowsAsync<ValidationException>(() => zap.Domains.DeleteAsync(".."))).Message);
            Assert.Equal("aid must not be empty", (await Assert.ThrowsAsync<ValidationException>(() => zap.Emails.Receiving.Attachments.GetAsync("m1", null!))).Message);
            Assert.Equal("event must not be empty", (await Assert.ThrowsAsync<ValidationException>(() => zap.Webhooks.Events.GetAsync("w1", string.Empty))).Message);
            Assert.Empty(h.Calls);
            await zap.Domains.DeleteAsync("a.b/..");
            Assert.Equal("https://api.test/v1/domains/a.b%2F..", h.Calls[0].Url);
        }

        [Fact]
        public async Task Refuses_an_empty_Idempotency_Key_before_sending()
        {
            var h = new FakeHandler().Answer(() => Http.Json(200, new { id = "e1" }));
            var zap = Clients.Make(h);
            var email = new SendEmailParams { To = new[] { "b@y.test" } };
            foreach (var options in new[]
            {
                new RequestOptions { IdempotencyKey = string.Empty },
                new RequestOptions { IdempotencyKey = "  " },
                new RequestOptions { Headers = new Dictionary<string, string> { ["idempotency-key"] = " " } },
                new RequestOptions { IdempotencyKey = "k", Headers = new Dictionary<string, string> { ["IDEMPOTENCY-KEY"] = string.Empty } },
            })
            {
                var e = await Assert.ThrowsAsync<ValidationException>(() => zap.Emails.SendAsync(email, options));
                Assert.Equal("invalid_parameter", e.Code);
                Assert.Equal("idempotencyKey must not be empty", e.Message);
            }
            await Assert.ThrowsAsync<ValidationException>(() => zap.Domains.CreateAsync(new DomainsCreateParams { Name = "a.test" },
                new RequestOptions { Headers = new Dictionary<string, string> { ["Idempotency-Key"] = string.Empty } }));
            Assert.Empty(h.Calls);
            await zap.Emails.SendAsync(email, new RequestOptions { IdempotencyKey = "a", Headers = new Dictionary<string, string> { ["idempotency-KEY"] = "b" } });
            Assert.Equal("b", h.Calls[0].Header("Idempotency-Key"));
            Assert.Single(h.Calls[0].Headers.Keys, k => string.Equals(k, "Idempotency-Key", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task Sends_empty_collections_the_caller_set()
        {
            var h = new FakeHandler().Answer(() => Http.Json(200, new { id = "x" }));
            var zap = Clients.Make(h);
            await zap.Threads.UpdateAsync("t1", new ThreadsUpdateParams { Metadata = new Dictionary<string, string>() });
            Assert.Equal("{\"metadata\":{}}", h.Calls[0].Body);
            await zap.Webhooks.UpdateAsync("w1", new WebhooksUpdateParams { Events = Array.Empty<string>(), Filter = new WebhookFilter { Actions = new List<string>() } });
            Assert.Equal("{\"events\":[],\"filter\":{\"actions\":[]}}", h.Calls[1].Body);
        }

        [Fact]
        public async Task Sends_an_empty_object_for_optional_parameters_left_out()
        {
            var h = new FakeHandler().Answer(() => Http.Json(200, new { id = "t1" }));
            await Clients.Make(h).Threads.UpdateAsync("t1");
            Assert.Equal("PATCH", h.Calls[0].Method);
            Assert.Equal("{}", h.Calls[0].Body);
        }

        [Fact]
        public async Task Lets_a_caller_set_a_content_header()
        {
            var h = new FakeHandler().Answer(() => Http.Json(200, new { id = "t1" }));
            var zap = Clients.Make(h);
            await zap.Threads.UpdateAsync("t1", new ThreadsUpdateParams { Read = true },
                new RequestOptions { Headers = new Dictionary<string, string> { ["Content-Type"] = "application/merge-patch+json" } });
            Assert.Equal("application/merge-patch+json", h.Calls[0].ContentType);
            await zap.Usage.GetAsync(new RequestOptions { Headers = new Dictionary<string, string> { ["Content-Type"] = "text/plain" } });
            Assert.Null(h.Calls[1].ContentType);
        }

        [Fact]
        public async Task Sends_explicit_nulls_only_when_asked()
        {
            var h = new FakeHandler().Answer(() => Http.Json(200, new { id = "k1" }));
            var zap = Clients.Make(h);
            await zap.ApiKeys.UpdateAsync("k1", new ApiKeysUpdateParams { Name = "n" });
            Assert.Equal("{\"name\":\"n\"}", h.Calls[0].Body);
            await zap.ApiKeys.UpdateAsync("k1", new ApiKeysUpdateParams { DomainId = null, Stream = EmailStream.Broadcast });
            Assert.Equal("{\"domain_id\":null,\"stream\":\"broadcast\"}", h.Calls[1].Body);
            var p = new ApiKeysUpdateParams { DomainId = "d1" };
            Assert.True(p.DomainId.HasValue);
            Assert.Equal("d1", p.DomainId.Value);
            Assert.False(new ApiKeysUpdateParams().DomainId.HasValue);
        }

        [Fact]
        public async Task Sends_a_batch_with_its_validation_mode()
        {
            var h = new FakeHandler().Answer(() => Http.Json(200, new { data = Array.Empty<object>() }));
            var zap = Clients.Make(h);
            var emails = new[] { new SendEmailParams { From = "a", To = new[] { "b" }, Subject = "s" } };
            await zap.Emails.SendBatchAsync(emails);
            Assert.Null(h.Calls[0].Header("x-batch-validation"));
            Assert.Equal("[{\"from\":\"a\",\"to\":[\"b\"],\"subject\":\"s\"}]", h.Calls[0].Body);
            await zap.Emails.SendBatchAsync(emails.Select(e => e), BatchValidation.Permissive);
            Assert.Equal("permissive", h.Calls[1].Header("x-batch-validation"));
            Assert.NotNull(h.Calls[1].Header("Idempotency-Key"));
            await Assert.ThrowsAsync<ArgumentNullException>(() => zap.Emails.SendBatchAsync(null!));
        }

        [Fact]
        public async Task Answers_nothing_for_204()
        {
            var h = new FakeHandler().Answer(Http.NoContent);
            var zap = Clients.Make(h);
            await zap.Domains.DeleteAsync("d1");
            var res = await zap.Domains.DeleteWithResponseAsync("d1");
            Assert.Equal(204, res.StatusCode);
            Assert.Equal(string.Empty, res.RawBody);
            Assert.Null(res.RequestId);
            Assert.Equal("DELETE", h.Calls[0].Method);
        }

        [Fact]
        public async Task Keeps_a_body_that_is_not_JSON_as_text()
        {
            var h = new FakeHandler().Answer(() => Http.Text(200, "plain"));
            var res = await Clients.Make(h).Usage.GetWithResponseAsync();
            Assert.Equal("plain", res.RawBody);
            Assert.Null(res.Data.Plan);
            var empty = new FakeHandler().Answer(() => Http.Text(200, string.Empty));
            Assert.Null((await Clients.Make(empty).Usage.GetAsync()).Sent);
            var array = new FakeHandler().Answer(() => Http.Text(200, "[1]"));
            Assert.Null((await Clients.Make(array).Usage.GetAsync()).Sent);
        }

        [Fact]
        public async Task Gives_the_status_headers_and_request_id_WithResponse()
        {
            var h = new FakeHandler().Answer(() => Http.Json(200, new { id = "e1" }, ("RateLimit-Remaining", "9")));
            var res = await Clients.Make(h).Emails.GetWithResponseAsync("e1");
            Assert.Equal("e1", res.Data.Id);
            Assert.Equal(200, res.StatusCode);
            Assert.Equal("req_1", res.RequestId);
            Assert.Equal("9", res.Headers["ratelimit-remaining"]);
            Assert.StartsWith("application/json", res.Headers["Content-Type"]);
            Assert.Equal("{\"id\":\"e1\"}", res.RawBody);
        }

        [Fact]
        public async Task Uploads_an_image_file_as_multipart()
        {
            var h = new FakeHandler().Answer(() => Http.Json(201, new { id = "img" }));
            var zap = Clients.Make(h);
            var asset = await zap.TemplateAssets.UploadFileAsync(new MemoryStream(new byte[] { 1, 2, 3 }), name: "logo.png");
            Assert.Equal("img", asset.Id);
            var call = h.Calls[0];
            Assert.Equal("POST", call.Method);
            Assert.Equal("https://api.test/v1/template-assets", call.Url);
            Assert.StartsWith("multipart/form-data", call.ContentType);
            Assert.Contains("name=file; filename=logo.png", call.Body);
            Assert.Contains("Content-Type: image/png", call.Body);
            Assert.Contains("name=name", call.Body);
            Assert.Null(call.Header("Idempotency-Key"));
            await zap.TemplateAssets.UploadFileAsync(new MemoryStream(new byte[] { 1 }), filename: "a.gif");
            Assert.Contains("filename=a.gif", h.Calls[1].Body);
            Assert.DoesNotContain("name=name", h.Calls[1].Body);
            await zap.TemplateAssets.UploadFileAsync(new MemoryStream(new byte[] { 1 }));
            Assert.Contains("filename=image", h.Calls[2].Body);
            Assert.Contains("Content-Type: application/octet-stream", h.Calls[2].Body);
            await Assert.ThrowsAsync<ArgumentNullException>(() => zap.TemplateAssets.UploadFileAsync(null!));
        }

        [Fact]
        public async Task Resends_the_same_upload_after_a_429()
        {
            var h = new FakeHandler().Answer(() => Http.Error(429, "rate_limit_exceeded", ("Retry-After", "1"))).Answer(() => Http.Json(201, new { id = "img" }));
            var delays = new List<TimeSpan>();
            await Clients.Make(h, delays).TemplateAssets.UploadFileAsync(new MemoryStream(Encoding.ASCII.GetBytes("GIF89a")), "a.gif");
            Assert.Equal(2, h.Calls.Count);
            Assert.Contains("GIF89a", h.Calls[1].Body);
            Assert.Equal(new[] { TimeSpan.FromSeconds(1) }, delays);
        }

        [Fact]
        public async Task Owns_only_the_HttpClient_it_made()
        {
            var h = new FakeHandler().Answer(() => Http.Json(200, new { id = "x" }));
            var http = new HttpClient(h);
            new ZapSMTPClient("k", new ZapSMTPClientOptions { HttpClient = http, BaseUrl = "https://api.test/v1" }).Dispose();
            await http.GetAsync("https://api.test/v1/usage"); // still usable
            new ZapSMTPClient("k").Dispose();
        }

        [Fact]
        public async Task Registers_with_dependency_injection()
        {
            var h = new FakeHandler().Answer(() => Http.Json(200, new { id = "e1" }));
            var services = new ServiceCollection();
            services.AddZapSMTP(o =>
            {
                o.ApiKey = "zap_di";
                o.BaseUrl = "https://di.test/v1";
                o.MaxRetries = 1;
                o.Timeout = TimeSpan.FromSeconds(5);
                o.DefaultHeaders = new Dictionary<string, string> { ["X-App"] = "a" };
            }).ConfigurePrimaryHttpMessageHandler(() => h);
            using var provider = services.BuildServiceProvider();
            var zap = provider.GetRequiredService<ZapSMTPClient>();
            Assert.Equal(1, zap.MaxRetries);
            Assert.Equal(TimeSpan.FromSeconds(5), zap.Timeout);
            await zap.Emails.GetAsync("e1");
            Assert.Equal("https://di.test/v1/emails/e1", h.Calls[0].Url);
            Assert.Equal("Bearer zap_di", h.Calls[0].Header("Authorization"));
            Assert.Equal("a", h.Calls[0].Header("X-App"));

            var plain = new ServiceCollection();
            plain.AddZapSMTP();
            plain.Configure<ZapSMTPClientOptions>(o => o.ApiKey = "zap_plain");
            using var p2 = plain.BuildServiceProvider();
            Assert.Equal(ZapSMTPClientOptions.DefaultBaseUrl, p2.GetRequiredService<ZapSMTPClient>().BaseUrl);
            Assert.Throws<ArgumentNullException>(() => ZapSMTPServiceCollectionExtensions.AddZapSMTP(null!));
        }

        [Fact]
        public void Services_can_be_mocked()
        {
            Assert.Null(new MockEmails().Receiving);
            var services = typeof(ZapSMTPClient).Assembly.GetTypes().Where(t => t.Name.EndsWith("Service", StringComparison.Ordinal) && t.IsPublic).ToList();
            Assert.True(services.Count > 20);
            foreach (var t in services) Assert.NotNull(Activator.CreateInstance(t, nonPublic: true));
        }

        private sealed class MockEmails : EmailsService
        {
        }

        private sealed class Sku
        {
            public override string ToString() => "sku-1";
        }

        [Fact]
        public async Task Every_method_takes_its_optional_parameters_left_out()
        {
            var h = new FakeHandler().Answer(() => Http.Json(200, new { }));
            var zap = Clients.Make(h);
            var calls = 0;
            foreach (var service in AllServices(zap))
            {
                foreach (var m in service.GetType().GetMethods().Where(m => m.Name.EndsWith("WithResponseAsync", StringComparison.Ordinal) || m.Name.EndsWith("AutoPagingAsync", StringComparison.Ordinal)))
                {
                    var ps = m.GetParameters();
                    var parameters = ps.FirstOrDefault(p => p.Name == "parameters");
                    if (parameters is null || !parameters.HasDefaultValue) continue;
                    var args = ps.Select(p => p.HasDefaultValue ? Type.Missing : (object)"x").ToArray();
                    var before = h.Calls.Count;
                    var result = m.Invoke(service, BindingFlags.OptionalParamBinding, null, args, null)!;
                    if (result is Task task) await task;
                    else await FirstItemAsync(result);
                    var call = h.Calls[before];
                    Assert.True(call.Method == "GET" ? call.Body is null : call.Body == "{}", $"{m.DeclaringType!.Name}.{m.Name}: {call.Method} {call.Body}");
                    Assert.DoesNotContain("?", call.Url);
                    calls++;
                }
            }
            Assert.True(calls > 20);
        }

        // walks an IAsyncEnumerable<T> of any T far enough to send its first request
        private static async Task FirstItemAsync(object enumerable)
        {
            var type = enumerable.GetType().GetInterfaces().First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>));
            var enumerator = type.GetMethod("GetAsyncEnumerator")!.Invoke(enumerable, new object[] { CancellationToken.None })!;
            var enumeratorType = typeof(IAsyncEnumerator<>).MakeGenericType(type.GetGenericArguments());
            var moved = (ValueTask<bool>)enumeratorType.GetMethod("MoveNextAsync")!.Invoke(enumerator, null)!;
            Assert.False(await moved);
        }

        private static IEnumerable<object> AllServices(object root)
        {
            foreach (var p in root.GetType().GetProperties().Where(p => p.PropertyType.Name.EndsWith("Service", StringComparison.Ordinal)))
            {
                var service = p.GetValue(root)!;
                yield return service;
                foreach (var child in AllServices(service)) yield return child;
            }
        }
    }
}
