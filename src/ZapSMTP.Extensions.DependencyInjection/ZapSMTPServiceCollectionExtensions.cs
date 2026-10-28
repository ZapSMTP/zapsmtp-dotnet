using System;
using Microsoft.Extensions.Options;
using ZapSMTP;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Registers <see cref="ZapSMTPClient"/> with dependency injection.</summary>
    public static class ZapSMTPServiceCollectionExtensions
    {
        /// <summary>The name of the HttpClient the client sends with.</summary>
        public const string HttpClientName = "ZapSMTP";

        /// <summary>
        /// Registers <see cref="ZapSMTPClient"/> as a typed client of IHttpClientFactory, configured from
        /// <see cref="ZapSMTPClientOptions"/> (API key, base URL, timeout, retries, headers). The key and the
        /// base URL fall back to ZAP_API_KEY and ZAP_BASE_URL.
        /// <code>
        /// builder.Services.AddZapSMTP(o => o.ApiKey = builder.Configuration["ZapSMTP:ApiKey"]);
        /// </code>
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configure">Sets the options.</param>
        /// <returns>The HttpClient's builder, to add handlers or configure the primary handler.</returns>
        public static IHttpClientBuilder AddZapSMTP(this IServiceCollection services, Action<ZapSMTPClientOptions>? configure = null)
        {
            if (services is null) throw new ArgumentNullException(nameof(services));
            var options = services.AddOptions<ZapSMTPClientOptions>();
            if (configure is not null) options.Configure(configure);
            return services.AddHttpClient(HttpClientName).AddTypedClient<ZapSMTPClient>((http, provider) =>
            {
                var o = provider.GetRequiredService<IOptions<ZapSMTPClientOptions>>().Value;
                http.Timeout = System.Threading.Timeout.InfiniteTimeSpan; // the client times each attempt itself
                return new ZapSMTPClient(o.ApiKey, new ZapSMTPClientOptions
                {
                    ApiKey = o.ApiKey,
                    BaseUrl = o.BaseUrl,
                    Timeout = o.Timeout,
                    MaxRetries = o.MaxRetries,
                    DefaultHeaders = o.DefaultHeaders,
                    HttpClient = http,
                });
            });
        }
    }
}
