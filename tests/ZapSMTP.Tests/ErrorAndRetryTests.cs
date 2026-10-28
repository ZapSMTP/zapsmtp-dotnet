using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using ZapSMTP.Internal;

namespace ZapSMTP.Tests
{
    public sealed class ErrorTests
    {
        public static IEnumerable<object[]> Table() => new[]
        {
            new object[] { 400, "validation_error", typeof(ValidationException) },
            new object[] { 401, "invalid_api_key", typeof(AuthenticationException) },
            new object[] { 403, "restricted_api_key", typeof(PermissionException) },
            new object[] { 404, "not_found", typeof(NotFoundException) },
            new object[] { 409, "conflict", typeof(ConflictException) },
            new object[] { 413, "payload_too_large", typeof(ValidationException) },
            new object[] { 422, "invalid_parameter", typeof(ValidationException) },
            new object[] { 429, "monthly_quota_exceeded", typeof(RateLimitException) },
            new object[] { 500, "internal_error", typeof(ServerException) },
            new object[] { 410, "link_expired", typeof(ZapSMTPException) },
        };

        [Theory]
        [MemberData(nameof(Table))]
        public async Task Maps_the_status_to_its_exception(int status, string code, Type type)
        {
            var h = new FakeHandler().Answer(() => Http.Error(status, code));
            var e = await Assert.ThrowsAnyAsync<ZapSMTPException>(() => Clients.Make(h, configure: o => o.MaxRetries = 0).Emails.GetAsync("x"));
            Assert.Equal(type, e.GetType());
            Assert.Equal(status, e.StatusCode);
            Assert.Equal(code, e.Code);
            Assert.Equal($"{code} happened", e.Message);
            Assert.Equal("invalid_request_error", e.Type);
            Assert.Equal("req_1", e.RequestId);
            Assert.Equal("req_1", e.Headers["x-request-id"]);
            Assert.Equal(code, e.Body!.Value.GetProperty("name").GetString());
            Assert.Contains(code, e.RawBody);
        }

        [Fact]
        public async Task Reads_the_flat_error_shape_and_a_body_that_is_not_JSON()
        {
            var flat = new FakeHandler().Answer(() => Http.Json(422, new { statusCode = 422, name = "missing_required_field", message = "subject is required" }));
            var e = await Assert.ThrowsAsync<ValidationException>(() => Clients.Make(flat).Emails.GetAsync("x"));
            Assert.Equal("missing_required_field", e.Code);
            Assert.Equal("subject is required", e.Message);
            Assert.Equal(string.Empty, e.Type);
            var text = new FakeHandler().Answer(() => Http.Text(418, "bad gateway"));
            var t = await Assert.ThrowsAsync<ZapSMTPException>(() => Clients.Make(text).Emails.GetAsync("x"));
            Assert.Equal(418, t.StatusCode);
            Assert.Equal(string.Empty, t.Code);
            Assert.Equal("HTTP 418", t.Message);
            Assert.Null(t.RequestId);
            Assert.Null(t.Body);
            var odd = new FakeHandler().Answer(() => Http.Json(400, new { error = "nope", name = 5 }));
            var o = await Assert.ThrowsAsync<ValidationException>(() => Clients.Make(odd).Emails.GetAsync("x"));
            Assert.Equal(string.Empty, o.Code);
            Assert.Equal("HTTP 400", o.Message);
        }

        [Fact]
        public async Task Gives_Retry_After_on_rate_limits()
        {
            var h = new FakeHandler().Answer(() => Http.Error(429, "monthly_quota_exceeded", ("Retry-After", "7")));
            var e = await Assert.ThrowsAsync<RateLimitException>(() => Clients.Make(h).Emails.GetAsync("x"));
            Assert.Equal(TimeSpan.FromSeconds(7), e.RetryAfter);
            Assert.Single(h.Calls); // quota errors are not retried
            Assert.Null(new RateLimitException("x").RetryAfter);
            var huge = new FakeHandler().Answer(() => Http.Error(429, "monthly_quota_exceeded", ("Retry-After", "1e300")));
            var capped = await Assert.ThrowsAsync<RateLimitException>(() => Clients.Make(huge).Emails.GetAsync("x"));
            Assert.Equal(TimeSpan.FromDays(1), capped.RetryAfter);
        }

        [Fact]
        public void Every_exception_can_be_made_by_hand()
        {
            var all = new ZapSMTPException[]
            {
                new AuthenticationException("m"), new PermissionException("m"), new NotFoundException("m"), new ConflictException("m"),
                new ValidationException("m"), new RateLimitException("m"), new ServerException("m"), new ConnectionException("m"),
                new WebhookVerificationException("m"), new ZapSMTPException("m"),
            };
            foreach (var e in all)
            {
                Assert.Equal("m", e.Message);
                Assert.Equal(0, e.StatusCode);
                Assert.Equal(string.Empty, e.Code);
                Assert.Equal(string.Empty, e.Type);
                Assert.Empty(e.Headers);
                Assert.Null(e.RequestId);
                Assert.Null(e.Body);
                Assert.Null(e.RawBody);
            }
        }

        [Fact]
        public async Task Wraps_network_failures()
        {
            var cause = new HttpRequestException("connection refused");
            var h = new FakeHandler().Throw(cause);
            var e = await Assert.ThrowsAsync<ConnectionException>(() => Clients.Make(h, configure: o => o.MaxRetries = 0).Emails.GetAsync("x"));
            Assert.Equal(0, e.StatusCode);
            Assert.Same(cause, e.InnerException);
            Assert.Equal("connection refused", e.Message);
        }

        [Fact]
        public async Task Times_out()
        {
            var h = new FakeHandler().Answer(async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return Http.Json(200, new { });
            });
            var zap = Clients.Make(h, configure: o =>
            {
                o.MaxRetries = 0;
                o.Timeout = TimeSpan.FromMilliseconds(20);
            });
            var e = await Assert.ThrowsAsync<ConnectionException>(() => zap.Emails.GetAsync("x"));
            Assert.Matches("timed out after 20 ms", e.Message);
            Assert.IsType<TimeoutException>(e.InnerException);
            var per = await Assert.ThrowsAsync<ConnectionException>(() => Clients.Make(h).Emails.GetAsync("x", new RequestOptions { Timeout = TimeSpan.FromMilliseconds(30), MaxRetries = 0 }));
            Assert.Matches("timed out after 30 ms", per.Message);
        }

        [Fact]
        public async Task Treats_a_cancellation_the_SDK_did_not_ask_for_as_a_connection_failure()
        {
            // HttpClient.Timeout of a caller's HttpClient, for example
            var h = new FakeHandler().Throw(new TaskCanceledException("HttpClient.Timeout"));
            var e = await Assert.ThrowsAsync<ConnectionException>(() => Clients.Make(h, configure: o => o.MaxRetries = 0).Emails.GetAsync("x"));
            Assert.Equal("HttpClient.Timeout", e.Message);
        }

        [Fact]
        public async Task Stops_on_the_callers_cancellation_without_retrying()
        {
            using var cts = new CancellationTokenSource();
            var h = new FakeHandler().Answer(async (_, ct) =>
            {
                cts.Cancel();
                await Task.Delay(Timeout.Infinite, ct);
                return Http.Json(200, new { });
            });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Clients.Make(h).Emails.GetAsync("x", cancellationToken: cts.Token));
            Assert.Single(h.Calls);
        }

        [Fact]
        public async Task An_already_cancelled_token_prevents_the_request_from_being_sent()
        {
            var h = new FakeHandler().Answer(() => Http.Json(200, new { }));
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Clients.Make(h).Emails.GetAsync("email-1", cancellationToken: cts.Token));
            Assert.Empty(h.Calls);
        }

        [Fact]
        public async Task The_timeout_also_bounds_reading_the_body()
        {
            var h = new FakeHandler().Answer(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) });
            var zap = Clients.Make(h, configure: o =>
            {
                o.MaxRetries = 0;
                o.Timeout = TimeSpan.FromMilliseconds(20);
            });
            var e = await Assert.ThrowsAsync<ConnectionException>(() => zap.Emails.GetAsync("email-1"));
            Assert.Matches("timed out after 20 ms", e.Message);
            Assert.IsType<TimeoutException>(e.InnerException);
            Assert.Equal(200, e.StatusCode);
        }

        [Fact]
        public async Task A_broken_body_is_a_connection_error_and_a_safe_request_is_retried()
        {
            var failure = new IOException("connection reset while reading body");
            HttpResponseMessage Broken()
            {
                var res = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream(failure)) };
                res.Headers.TryAddWithoutValidation("X-Request-Id", "req_b");
                return res;
            }
            var once = new FakeHandler().Answer(Broken);
            var e = await Assert.ThrowsAsync<ConnectionException>(() => Clients.Make(once, configure: o => o.MaxRetries = 0).Emails.GetAsync("email-1"));
            Assert.Same(failure, e.InnerException);
            Assert.Equal("connection reset while reading body", e.Message);
            Assert.Equal(200, e.StatusCode); // the status and headers arrived before the body broke off
            Assert.Equal("req_b", e.RequestId);
            Assert.Equal("req_b", e.Headers["X-Request-Id"]);
            var retried = new FakeHandler().Answer(Broken).Answer(() => Http.Json(200, new { id = "e1" }));
            var delays = new List<TimeSpan>();
            Assert.Equal("e1", (await Clients.Make(retried, delays).Emails.GetAsync("email-1")).Id);
            Assert.Equal(2, retried.Calls.Count);
            Assert.Single(delays);
        }

        /// <summary>Sends a byte, then nothing until cancelled.</summary>
        private sealed class StallingStream : Stream
        {
            private bool _sent;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (!_sent)
                {
                    _sent = true;
                    buffer.Span[0] = (byte)'{';
                    return 1;
                }
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        private sealed class BrokenStream : Stream
        {
            private readonly Exception _error;

            public BrokenStream(Exception error) => _error = error;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw _error;
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException<int>(_error);
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromException<int>(_error);
        }
    }

    public sealed class RetryTests
    {
        [Fact]
        public async Task Retries_safe_requests_on_5xx_and_network_errors_honouring_Retry_After()
        {
            var h = new FakeHandler()
                .Answer(() => Http.Error(503, "service_unavailable", ("Retry-After", "2")))
                .Throw(new HttpRequestException("reset"))
                .Answer(() => Http.Json(200, new { id = "e1" }));
            var delays = new List<TimeSpan>();
            Assert.Equal("e1", (await Clients.Make(h, delays).Emails.GetAsync("e1")).Id);
            Assert.Equal(3, h.Calls.Count);
            Assert.Equal(TimeSpan.FromSeconds(2), delays[0]);
            Assert.InRange(delays[1].TotalMilliseconds, 500, 1000); // attempt 1: 1 s cap, half of it at least
        }

        [Fact]
        public async Task Retries_a_send_with_the_same_Idempotency_Key()
        {
            var h = new FakeHandler().Answer(() => Http.Error(502, "bad_gateway")).Answer(() => Http.Json(200, new { id = "e1" }));
            await Clients.Make(h).Emails.SendAsync(new SendEmailParams { From = "a", To = new[] { "b" }, Subject = "s" });
            Assert.Equal(2, h.Calls.Count);
            Assert.Equal(h.Calls[0].Header("Idempotency-Key"), h.Calls[1].Header("Idempotency-Key"));
            Assert.Equal(h.Calls[0].Body, h.Calls[1].Body);
        }

        [Fact]
        public async Task Does_not_retry_a_create_on_5xx_or_a_lost_connection_but_does_on_429()
        {
            var five = new FakeHandler().Answer(() => Http.Error(500, "internal_error"));
            await Assert.ThrowsAsync<ServerException>(() => Clients.Make(five).Domains.CreateAsync(new DomainsCreateParams { Name = "a.test" }));
            Assert.Single(five.Calls);
            var lost = new FakeHandler().Throw(new HttpRequestException("reset"));
            await Assert.ThrowsAsync<ConnectionException>(() => Clients.Make(lost).Domains.CreateAsync(new DomainsCreateParams { Name = "a.test" }));
            Assert.Single(lost.Calls);
            var limited = new FakeHandler().Answer(() => Http.Error(429, "rate_limit_exceeded", ("Retry-After", "1"))).Answer(() => Http.Json(201, new { id = "d1" }));
            Assert.Equal("d1", (await Clients.Make(limited).Domains.CreateAsync(new DomainsCreateParams { Name = "a.test" })).Id);
            var busy = new FakeHandler().Answer(() => Http.Error(409, "idempotency_request_in_progress")).Answer(() => Http.Json(200, new { id = "e1" }));
            await Clients.Make(busy).Emails.SendAsync(new SendEmailParams { To = new[] { "b" } });
            Assert.Equal(2, busy.Calls.Count);
        }

        [Fact]
        public async Task Gives_up_after_MaxRetries()
        {
            var h = new FakeHandler().Answer(() => Http.Error(503, "service_unavailable", ("Retry-After", "0")));
            await Assert.ThrowsAsync<ServerException>(() => Clients.Make(h, configure: o => o.MaxRetries = 3).Emails.GetAsync("x"));
            Assert.Equal(4, h.Calls.Count);
            var once = new FakeHandler().Answer(() => Http.Error(503, "service_unavailable"));
            await Assert.ThrowsAsync<ServerException>(() => Clients.Make(once).Emails.GetAsync("x", new RequestOptions { MaxRetries = 0 }));
            Assert.Single(once.Calls);
        }

        [Fact]
        public async Task Waits_for_real_by_default()
        {
            var h = new FakeHandler().Answer(() => Http.Error(503, "s", ("Retry-After", "0.01"))).Answer(() => Http.Json(200, new { id = "e1" }));
            using var zap = new ZapSMTPClient("k", new ZapSMTPClientOptions { BaseUrl = "https://api.test/v1", HttpClient = new HttpClient(h) });
            Assert.Equal("e1", (await zap.Emails.GetAsync("e1")).Id);
            Assert.True(Math.Abs((zap.Core.Clock() - DateTimeOffset.UtcNow).TotalSeconds) < 5);
        }

        [Fact]
        public void Decides_what_is_worth_retrying()
        {
            Assert.True(RetryPolicy.Retryable(429, "overloaded", false));
            Assert.False(RetryPolicy.Retryable(429, "daily_quota_exceeded", true));
            Assert.False(RetryPolicy.Retryable(429, "thread_reply_limit", true));
            Assert.True(RetryPolicy.Retryable(409, "idempotency_request_in_progress", true));
            Assert.False(RetryPolicy.Retryable(409, "idempotency_key_reused", true));
            Assert.True(RetryPolicy.Retryable(503, "service_unavailable", true));
            Assert.False(RetryPolicy.Retryable(503, "service_unavailable", false));
            Assert.True(RetryPolicy.Retryable(408, "timeout", true));
            Assert.False(RetryPolicy.Retryable(422, "invalid_parameter", true));
        }

        [Fact]
        public void Backs_off_exponentially_with_jitter_capped_or_as_Retry_After_says()
        {
            for (var a = 0; a < 8; a++)
            {
                var cap = Math.Min(500 * Math.Pow(2, a), 8000);
                var d = RetryPolicy.Backoff(a, null).TotalMilliseconds;
                Assert.InRange(d, cap / 2, cap);
            }
            Assert.Equal(TimeSpan.FromSeconds(3), RetryPolicy.Backoff(0, 3));
            Assert.Equal(TimeSpan.FromSeconds(60), RetryPolicy.Backoff(0, 600));
        }

        [Fact]
        public async Task Reads_Retry_After_as_an_HTTP_date()
        {
            var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var h = new FakeHandler().Answer(() => Http.Error(503, "s", ("Retry-After", "Thu, 01 Jan 2026 00:00:03 GMT"))).Answer(() => Http.Json(200, new { }));
            var delays = new List<TimeSpan>();
            var zap = Clients.Make(h, delays);
            zap.Core.Clock = () => now;
            await zap.Emails.GetAsync("x");
            Assert.Equal(new[] { TimeSpan.FromSeconds(3) }, delays);

            static IReadOnlyDictionary<string, string> H(string v) => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Retry-After"] = v };
            Assert.Null(RetryPolicy.RetryAfterSeconds(H("soon"), now));
            Assert.Null(RetryPolicy.RetryAfterSeconds(H("NaN"), now));
            Assert.Null(RetryPolicy.RetryAfterSeconds(H(" "), now));
            Assert.Null(RetryPolicy.RetryAfterSeconds(new Dictionary<string, string>(), now));
            Assert.Equal(0, RetryPolicy.RetryAfterSeconds(H("-3"), now));
            Assert.Equal(0, RetryPolicy.RetryAfterSeconds(H("Wed, 01 Jan 2025 00:00:00 GMT"), now));
            var bad = new FakeHandler().Answer(() => Http.Error(429, "overloaded", ("Retry-After", "soon")));
            var e = await Assert.ThrowsAsync<RateLimitException>(() => Clients.Make(bad, configure: o => o.MaxRetries = 0).Emails.GetAsync("x"));
            Assert.Null(e.RetryAfter);
        }
    }
}
