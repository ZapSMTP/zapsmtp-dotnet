using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace ZapSMTP.Tests
{
    public sealed class codexSdkdotnetRetrySafetyTests
    {
        [Theory]
        [InlineData("order-123", false)]
        [InlineData("", false)]
        [InlineData("", true)]
        public async Task TestCodexSdkdotnetLostSendResponseCannotDeliverTwice(string key, bool useHeader)
        {
            var accepted = 0;
            var attempts = 0;
            var claimedKeys = new HashSet<string>(StringComparer.Ordinal);
            var h = new FakeHandler().Answer((request, _) =>
            {
                // Match backend/internal/api/emails.go: an empty key bypasses the
                // idempotency store. Nonempty keys replay the original acceptance.
                var actualKey = request.Headers.TryGetValues("Idempotency-Key", out var values)
                    ? string.Join(", ", values) : string.Empty;
                if (actualKey.Length == 0 || claimedKeys.Add(actualKey)) accepted++;
                attempts++;
                if (attempts == 1)
                    return Task.FromException<HttpResponseMessage>(new HttpRequestException("response lost after acceptance"));
                return Task.FromResult(Http.Json(200, new { id = "accepted-email" }));
            });
            using var zap = Clients.Make(h, configure: o => o.MaxRetries = 1);
            var options = useHeader
                ? new RequestOptions { Headers = new Dictionary<string, string> { ["Idempotency-Key"] = key } }
                : new RequestOptions { IdempotencyKey = key };

            // Rejecting an empty key, generating a real key, or refusing an unsafe
            // retry are all safe outcomes. Accepting the same email twice is not.
            var error = await Record.ExceptionAsync(() => zap.Emails.SendAsync(new SendEmailParams
            {
                From = "sender@example.test", To = new[] { "recipient@example.test" },
                Subject = "Receipt", Text = "One receipt only",
            }, options));

            Assert.InRange(accepted, 0, 1);
            if (key.Length != 0)
            {
                Assert.Null(error);
                Assert.Equal(1, accepted);
                Assert.Equal(2, attempts);
            }
            else if (error is not null)
            {
                Assert.True(error is ArgumentException || error is ZapSMTPException, error.ToString());
            }
        }

        [Fact]
        public async Task TestCodexSdkdotnetHugeRetryAfterDoesNotBreakErrorInspection()
        {
            var h = new FakeHandler().Answer(() => Http.Error(429, "rate_limit_exceeded", ("Retry-After", "1e100")));
            using var zap = Clients.Make(h, configure: o => o.MaxRetries = 0);
            var error = await Assert.ThrowsAsync<RateLimitException>(() => zap.Emails.GetAsync("email-1"));
            // An upstream's malformed/huge delay must not make the exception's
            // public metadata getter throw while handling the original error.
            var inspectionError = Record.Exception(() => { _ = error.RetryAfter; });
            Assert.Null(inspectionError);
            Assert.Equal(429, error.StatusCode);
            Assert.Equal("rate_limit_exceeded", error.Code);
            Assert.Single(h.Calls);
        }
    }
}
