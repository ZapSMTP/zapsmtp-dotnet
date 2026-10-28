using System;
using ZapSMTP.Internal;

namespace ZapSMTP
{
    /// <summary>
    /// The ZapSMTP API client. Make one and keep it: it is thread-safe and reuses its connections.
    /// <code>
    /// var zap = new ZapSMTPClient("zap_..."); // or set ZAP_API_KEY and call new ZapSMTPClient()
    /// var sent = await zap.Emails.SendAsync(new SendEmailParams { From = "...", To = new[] { "..." }, Subject = "...", Html = "..." });
    /// </code>
    /// </summary>
    public partial class ZapSMTPClient : IDisposable
    {
        private readonly ApiClient _client;

        /// <summary>A client with <paramref name="apiKey"/>, else the options' key, else ZAP_API_KEY.</summary>
        /// <param name="apiKey">The API key (zap_...).</param>
        /// <param name="options">Base URL, timeout, retries, headers and the HttpClient to use.</param>
        /// <exception cref="ZapSMTPException">No API key was given or found in ZAP_API_KEY.</exception>
        public ZapSMTPClient(string? apiKey = null, ZapSMTPClientOptions? options = null)
            : this(new ApiClient(apiKey, options ?? new ZapSMTPClientOptions()))
        {
        }

        /// <summary>The SDK's version.</summary>
        public static string Version => SdkInfo.Version;

        /// <summary>The API's address, such as https://api.zapsmtp.com/v1.</summary>
        public string BaseUrl => _client.BaseUrl;

        /// <summary>The time each attempt may take.</summary>
        public TimeSpan Timeout => _client.Timeout;

        /// <summary>Retries of a failed request that is safe to retry.</summary>
        public int MaxRetries => _client.MaxRetries;

        internal ApiClient Core => _client;

        /// <summary>Releases the HttpClient the client made; one passed in the options is left alone.</summary>
        public void Dispose()
        {
            _client.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
