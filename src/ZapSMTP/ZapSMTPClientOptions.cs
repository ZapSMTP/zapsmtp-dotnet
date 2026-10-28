using System;
using System.Collections.Generic;
using System.Net.Http;

namespace ZapSMTP
{
    /// <summary>Settings of a <see cref="ZapSMTPClient"/>.</summary>
    public sealed class ZapSMTPClientOptions
    {
        /// <summary>The API's address used when neither <see cref="BaseUrl"/> nor ZAP_BASE_URL is set.</summary>
        public const string DefaultBaseUrl = "https://api.zapsmtp.com/v1";

        /// <summary>The API key (zap_...); defaults to the ZAP_API_KEY environment variable.</summary>
        public string? ApiKey { get; set; }

        /// <summary>The API's address; defaults to ZAP_BASE_URL, else <see cref="DefaultBaseUrl"/>.</summary>
        public string? BaseUrl { get; set; }

        /// <summary>The time each attempt may take, the answer's body included (default 60 s). <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> turns it off.</summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>Retries of a failed request that is safe to retry (default 2).</summary>
        public int MaxRetries { get; set; } = 2;

        /// <summary>Headers sent with every request.</summary>
        public IDictionary<string, string>? DefaultHeaders { get; set; }

        /// <summary>
        /// The HttpClient to send with, for example one from IHttpClientFactory; the client does not dispose
        /// it. By default the client makes and owns its own.
        /// </summary>
        public HttpClient? HttpClient { get; set; }
    }
}
