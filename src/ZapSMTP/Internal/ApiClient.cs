using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ZapSMTP.Internal
{
    /// <summary>The SDK's core: HTTP, retries, errors and paging. The generated services call it.</summary>
    internal sealed class ApiClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly bool _ownsHttp;
        private readonly Dictionary<string, string> _defaultHeaders;

        public ApiClient(string? apiKey, ZapSMTPClientOptions options)
        {
            var key = apiKey ?? options.ApiKey ?? Environment.GetEnvironmentVariable("ZAP_API_KEY");
            if (string.IsNullOrEmpty(key)) throw new ZapSMTPException("Missing API key: pass it to new ZapSMTPClient(...) or set ZAP_API_KEY");
            if (options.Timeout <= TimeSpan.Zero && options.Timeout != System.Threading.Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(options), "Timeout must be positive, or Timeout.InfiniteTimeSpan");
            if (options.MaxRetries < 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxRetries must not be negative");
            ApiKey = key!;
            BaseUrl = NonEmpty(options.BaseUrl) ?? NonEmpty(Environment.GetEnvironmentVariable("ZAP_BASE_URL")) ?? ZapSMTPClientOptions.DefaultBaseUrl;
            BaseUrl = BaseUrl.TrimEnd('/');
            Timeout = options.Timeout;
            MaxRetries = options.MaxRetries;
            _defaultHeaders = new Dictionary<string, string>(options.DefaultHeaders ?? new Dictionary<string, string>());
            _ownsHttp = options.HttpClient is null;
            _http = options.HttpClient ?? CreateHttpClient();
        }

        public string ApiKey { get; }

        public string BaseUrl { get; }

        public TimeSpan Timeout { get; }

        public int MaxRetries { get; }

        /// <summary>Waits between attempts; tests replace it.</summary>
        internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

        /// <summary>The time Retry-After dates are measured from; tests replace it.</summary>
        internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

        public void Dispose()
        {
            if (_ownsHttp) _http.Dispose();
        }

        private static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

        private static HttpClient CreateHttpClient()
        {
#if NETSTANDARD2_0
            var handler = new HttpClientHandler();
#else
            // pooled connections are renewed so that DNS changes are seen
            var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
#endif
            return new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        }

        // ---- helpers for the generated services ---------------------------------

        /// <summary>
        /// One path segment, escaped. An empty value, "." or ".." can be no id: URL parsing would resolve
        /// them into another path, so they are refused before anything is sent.
        /// </summary>
        public static string Segment(string? value, string name)
        {
            if (string.IsNullOrEmpty(value)) throw ApiError.Invalid($"{name} must not be empty");
            if (value is "." or "..") throw ApiError.Invalid($"{name} must not be \"{value}\"");
            return Uri.EscapeDataString(value);
        }

        public static T Required<T>(T? value, string name) where T : class => value ?? throw new ArgumentNullException(name);

        public static IReadOnlyList<T> ToList<T>(IEnumerable<T>? items, string name) => Required(items, name).ToList();

        public static IReadOnlyDictionary<string, string>? Header(string name, string? value) =>
            value is null ? null : new Dictionary<string, string> { [name] = value };

        // ---- requests ---------------------------------------------------------------

        public async Task<ZapResponse> SendAsync(ApiRequest request, RequestOptions? options, CancellationToken cancellationToken) =>
            await ExecuteAsync(request, options, cancellationToken).ConfigureAwait(false);

        public async Task<ZapResponse<T>> SendAsync<T>(ApiRequest request, RequestOptions? options, CancellationToken cancellationToken)
            where T : class, new()
        {
            var response = await ExecuteAsync(request, options, cancellationToken).ConfigureAwait(false);
            return new ZapResponse<T>(ZapJson.Decode<T>(response.RawBody), response);
        }

        public async Task<ZapResponse<Page<T>>> SendPageAsync<T>(ApiRequest request, Paging paging, RequestOptions? options, CancellationToken cancellationToken)
            where T : class
        {
            var response = await ExecuteAsync(request, options, cancellationToken).ConfigureAwait(false);
            var page = Page<T>.Parse(response.RawBody, request, paging,
                async (next, ct) => (await SendPageAsync<T>(next, paging, options, ct).ConfigureAwait(false)).Data);
            return new ZapResponse<Page<T>>(page, response);
        }

        public async IAsyncEnumerable<T> AutoPageAsync<T>(ApiRequest request, Paging paging, RequestOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default) where T : class
        {
            Page<T>? page = (await SendPageAsync<T>(request, paging, options, cancellationToken).ConfigureAwait(false)).Data;
            while (page is not null)
            {
                foreach (var item in page.Data)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return item;
                }
                page = await page.GetNextPageAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<ZapResponse> ExecuteAsync(ApiRequest request, RequestOptions? options, CancellationToken cancellationToken)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Bearer " + ApiKey,
                ["Accept"] = "application/json",
                ["User-Agent"] = SdkInfo.UserAgent,
            };
            Merge(headers, _defaultHeaders);
            Merge(headers, request.Headers);
            if (request.Idempotent) headers["Idempotency-Key"] = options?.IdempotencyKey ?? Guid.NewGuid().ToString();
            Merge(headers, options?.Headers); // the dictionary ignores case: a caller's header replaces ours whatever its case
            // an empty key turns deduplication off on the server, so a retried send could go out twice
            if (headers.TryGetValue("Idempotency-Key", out var key) && string.IsNullOrWhiteSpace(key))
                throw ApiError.Invalid("idempotencyKey must not be empty");
            var body = request.Body is null ? null : JsonSerializer.SerializeToUtf8Bytes(request.Body, request.Body.GetType(), ZapJson.Options);
            var query = request.Query?.ToString();
            var url = BaseUrl + request.Path + (string.IsNullOrEmpty(query) ? string.Empty : "?" + query);
            var maxRetries = options?.MaxRetries ?? MaxRetries;
            var timeout = options?.Timeout ?? Timeout;
            var safe = request.Method is "GET" or "PUT" or "DELETE" || request.Idempotent;

            for (var attempt = 0; ; attempt++)
            {
                ZapResponse response;
                try
                {
                    response = await AttemptAsync(request, url, headers, body, timeout, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // a timeout or a broken connection (sending, or reading the answer): the request
                    // may have reached the server, so only a request that is safe to repeat is sent again
                    if (attempt < maxRetries && safe)
                    {
                        await Delay(RetryPolicy.Backoff(attempt, null), cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    throw ex is BrokenBodyException broken
                        ? new ConnectionException(broken.Message, broken.InnerException!, broken.Response)
                        : new ConnectionException(ex.Message, ex);
                }
                if (response.StatusCode is >= 200 and < 300) return response;
                var error = ApiError.ToException(response.StatusCode, response.Headers, response.RawBody);
                if (attempt < maxRetries && RetryPolicy.Retryable(response.StatusCode, error.Code, safe))
                {
                    await Delay(RetryPolicy.Backoff(attempt, RetryPolicy.RetryAfterSeconds(response.Headers, Clock())), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                throw error;
            }
        }

        private static void Merge(Dictionary<string, string> into, IEnumerable<KeyValuePair<string, string>>? from)
        {
            if (from is null) return;
            foreach (var pair in from) into[pair.Key] = pair.Value;
        }

        // One attempt, the body read included: the timeout and the caller's token cover a server that
        // sends its headers and then stalls.
        private async Task<ZapResponse> AttemptAsync(ApiRequest request, string url, Dictionary<string, string> headers, byte[]? body,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            using var message = new HttpRequestMessage(new HttpMethod(request.Method), url);
            if (request.Form is not null) message.Content = request.Form.ToContent();
            else if (body is not null)
            {
                message.Content = new ByteArrayContent(body);
                message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }
            foreach (var pair in headers)
            {
                // a content header (Content-Type) given by the caller goes on the content
                if (!message.Headers.TryAddWithoutValidation(pair.Key, pair.Value) && message.Content is not null)
                {
                    message.Content.Headers.Remove(pair.Key);
                    message.Content.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
                }
            }
            try
            {
                using var res = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                var status = (int)res.StatusCode;
                var responseHeaders = Headers(res);
                string text;
                try
                {
                    text = await ReadBodyAsync(res.Content, cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // the answer broke off: its status and headers stay known
                    var cause = cts.IsCancellationRequested ? TimedOut(timeout, ex) : ex;
                    throw new BrokenBodyException(new ZapResponse(status, responseHeaders, string.Empty), cause);
                }
                return new ZapResponse(status, responseHeaders, text);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && cts.IsCancellationRequested)
            {
                throw TimedOut(timeout, ex);
            }
        }

        private static TimeoutException TimedOut(TimeSpan timeout, Exception cause) =>
            new($"timed out after {(long)timeout.TotalMilliseconds} ms", cause);

        private static async Task<string> ReadBodyAsync(HttpContent content, CancellationToken cancellationToken)
        {
            using var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, 81920, cancellationToken).ConfigureAwait(false);
            buffer.Position = 0;
            using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        /// <summary>The body of an answer broke off after its headers arrived.</summary>
        private sealed class BrokenBodyException : Exception
        {
            public BrokenBodyException(ZapResponse response, Exception cause)
                : base(cause.Message, cause)
            {
                Response = response;
            }

            public ZapResponse Response { get; }
        }

        private static IReadOnlyDictionary<string, string> Headers(HttpResponseMessage res)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in res.Headers.Concat(res.Content.Headers)) headers[h.Key] = string.Join(", ", h.Value);
            return headers;
        }
    }
}
