using System;
using System.Collections.Generic;

namespace ZapSMTP
{
    /// <summary>Options of one request; every API method takes them.</summary>
    public sealed class RequestOptions
    {
        /// <summary>
        /// Sent as Idempotency-Key on operations that take one (sends and replies); otherwise the SDK makes a
        /// random one, kept the same across retries.
        /// </summary>
        public string? IdempotencyKey { get; init; }

        /// <summary>Extra headers for this request; they override the SDK's own.</summary>
        public IReadOnlyDictionary<string, string>? Headers { get; init; }

        /// <summary>The time each attempt may take, the answer's body included (default: the client's).</summary>
        public TimeSpan? Timeout { get; init; }

        /// <summary>Retries of a failed request that is safe to retry (default: the client's).</summary>
        public int? MaxRetries { get; init; }
    }

    /// <summary>How a batch send validates its emails (<c>x-batch-validation</c>).</summary>
    public static class BatchValidation
    {
        /// <summary>The default: if any email fails validation nothing is sent and the call fails with 422.</summary>
        public const string Strict = "strict";

        /// <summary>Valid emails are sent; invalid ones are reported in the response's errors.</summary>
        public const string Permissive = "permissive";
    }
}
