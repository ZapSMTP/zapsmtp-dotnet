using System;
using System.Collections.Generic;
using System.Globalization;

namespace ZapSMTP.Internal
{
    /// <summary>When and how long to wait before trying a request again (docs/DESIGN-sdks.md, Retries).</summary>
    internal static class RetryPolicy
    {
        private static readonly HashSet<int> RetryStatuses = new() { 408, 429, 500, 502, 503, 504 };
        private static readonly HashSet<string> NoRetry429 = new() { "monthly_quota_exceeded", "daily_quota_exceeded", "thread_reply_limit" };
        private static readonly Random Jitter = new();

        /// <summary>Whether an error answer is worth another attempt.</summary>
        public static bool Retryable(int status, string code, bool safe)
        {
            if (status == 429) return !NoRetry429.Contains(code); // refused before any work: always safe
            if (status == 409) return code == "idempotency_request_in_progress";
            return safe && RetryStatuses.Contains(status);
        }

        /// <summary>The wait before attempt number attempt+1: Retry-After up to 60 s, else exponential with jitter.</summary>
        public static TimeSpan Backoff(int attempt, double? retryAfterSeconds)
        {
            if (retryAfterSeconds is { } seconds) return TimeSpan.FromSeconds(Math.Min(seconds, 60));
            var cap = Math.Min(500 * Math.Pow(2, attempt), 8000);
            double r;
            lock (Jitter) r = Jitter.NextDouble();
            return TimeSpan.FromMilliseconds(cap / 2 + r * (cap / 2));
        }

        /// <summary>Retry-After in seconds: a number, or an HTTP date relative to <paramref name="now"/>; null when absent or unreadable.</summary>
        public static double? RetryAfterSeconds(IReadOnlyDictionary<string, string> headers, DateTimeOffset now)
        {
            if (!headers.TryGetValue("retry-after", out var value) || string.IsNullOrWhiteSpace(value)) return null;
            // NaN and the infinities are not a number of seconds
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && Math.Abs(n) <= double.MaxValue) return Math.Max(0, n);
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) return Math.Max(0, (at - now).TotalSeconds);
            return null;
        }
    }
}
