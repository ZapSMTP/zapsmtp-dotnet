using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;
using ZapSMTP.Internal;

namespace ZapSMTP.Tests
{
    /// <summary>The arguments of one conformance case, typed by the generated calls.</summary>
    internal sealed class ConformanceCall
    {
        private readonly JsonElement _call;

        public ConformanceCall(JsonElement call)
        {
            _call = call;
        }

        public string Arg(int i) => _call.GetProperty("pathArgs")[i].GetString()!;

        /// <summary>The case's parameters as the typed params class; every field must land in a typed property.</summary>
        public T Params<T>() where T : ZapParams
        {
            var json = _call.GetProperty("params");
            var p = json.Deserialize<T>(ZapJson.Options)!;
            AssertJson.AllTyped(p);
            AssertJson.Same(json, JsonSerializer.SerializeToElement(p, ZapJson.Options));
            return p;
        }

        public List<T> Items<T>() where T : ZapParams =>
            _call.GetProperty("items").EnumerateArray().Select(e =>
            {
                var item = e.Deserialize<T>(ZapJson.Options)!;
                AssertJson.AllTyped(item);
                return item;
            }).ToList();

        public string? Option(string name) => _call.GetProperty("options").GetProperty(name).GetString();

        public static async Task<object?> OneAsync<T>(Task<T> call) => await call;

        public static async Task<object?> VoidAsync(Task call)
        {
            await call;
            return null;
        }

        /// <summary>The first page, and the same items through auto-paging (stopping before a second request).</summary>
        public static async Task<object?> PageAsync<T>(Task<Page<T>> first, Func<IAsyncEnumerable<T>> all) where T : class
        {
            var page = await first;
            var walked = new List<T>();
            if (page.Data.Count > 0)
            {
                await foreach (var item in all())
                {
                    walked.Add(item);
                    if (walked.Count == page.Data.Count) break;
                }
            }
            Assert.Equal(page.Data.Count, walked.Count);
            return page;
        }
    }

    /// <summary>The conformance server: ZAP_CONFORMANCE_URL, else node sdks/conformance/server.mjs started here.</summary>
    public sealed class ConformanceServer : IDisposable
    {
        private readonly Process? _process;

        public ConformanceServer()
        {
            var url = Environment.GetEnvironmentVariable("ZAP_CONFORMANCE_URL");
            if (string.IsNullOrEmpty(url))
            {
                _process = Process.Start(new ProcessStartInfo("node", $"\"{Path.Combine(Paths.Sdks, "conformance", "server.mjs")}\" 0")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                })!;
                var line = _process.StandardOutput.ReadLine() ?? string.Empty;
                url = $"http://127.0.0.1:{Regex.Match(line, @"listening (\d+)").Groups[1].Value}/v1";
            }
            Url = url!;
        }

        public string Url { get; }

        public void Dispose()
        {
            if (_process is null) return;
            _process.Kill();
            _process.Dispose();
        }
    }

    public sealed class ConformanceTests : IClassFixture<ConformanceServer>
    {
        private static readonly Dictionary<string, JsonElement> Cases =
            Paths.ReadJson("conformance", "cases.json").EnumerateArray().ToDictionary(c => c.GetProperty("id").GetString()!);

        private readonly ZapSMTPClient _zap;

        public ConformanceTests(ConformanceServer server)
        {
            _zap = new ZapSMTPClient("zap_test_conformance", new ZapSMTPClientOptions { BaseUrl = server.Url, MaxRetries = 0 });
        }

        public static IEnumerable<object[]> CaseIds() => Cases.Keys.Select(id => new object[] { id });

        [Fact]
        public void Every_operation_has_one_case_and_one_typed_call()
        {
            Assert.Equal(Paths.ReadJson("conformance", "cases.json").GetArrayLength(), Cases.Count);
            Assert.Equal(Cases.Keys.OrderBy(k => k, StringComparer.Ordinal), ConformanceCalls.ById.Keys.OrderBy(k => k, StringComparer.Ordinal));
        }

        [Theory]
        [MemberData(nameof(CaseIds))]
        public async Task Case(string id)
        {
            var c = Cases[id];
            var result = await ConformanceCalls.ById[id](_zap, new ConformanceCall(c.GetProperty("call")));
            var want = c.GetProperty("response").GetProperty("body");
            if (want.ValueKind == JsonValueKind.Null)
            {
                Assert.Null(result);
                return;
            }
            AssertJson.AllTyped(result);
            if (result!.GetType().IsGenericType && result.GetType().GetGenericTypeDefinition() == typeof(Page<>))
            {
                var data = result.GetType().GetProperty("Data")!.GetValue(result);
                AssertJson.Same(want.GetProperty("data"), JsonSerializer.SerializeToElement(data, ZapJson.Options));
                return;
            }
            AssertJson.Same(want, JsonSerializer.SerializeToElement(result, result!.GetType(), ZapJson.Options));
        }
    }
}
