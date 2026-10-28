using System.Collections.Generic;

namespace ZapSMTP
{
    /// <summary>What the API answered besides the body: from the <c>...WithResponseAsync</c> methods.</summary>
    public class ZapResponse
    {
        internal ZapResponse(int statusCode, IReadOnlyDictionary<string, string> headers, string rawBody)
        {
            StatusCode = statusCode;
            Headers = headers;
            RawBody = rawBody;
            headers.TryGetValue("x-request-id", out var requestId);
            RequestId = requestId;
        }

        /// <summary>The HTTP status.</summary>
        public int StatusCode { get; }

        /// <summary>
        /// The answer's headers (names are case-insensitive), such as <c>RateLimit-Remaining</c> and the
        /// quota headers.
        /// </summary>
        public IReadOnlyDictionary<string, string> Headers { get; }

        /// <summary>X-Request-Id: quote it to support.</summary>
        public string? RequestId { get; }

        /// <summary>The body as received; empty for 204.</summary>
        public string RawBody { get; }
    }

    /// <summary>A decoded answer with its status and headers.</summary>
    /// <typeparam name="T">The decoded body.</typeparam>
    public sealed class ZapResponse<T> : ZapResponse
    {
        internal ZapResponse(T data, ZapResponse response)
            : base(response.StatusCode, response.Headers, response.RawBody)
        {
            Data = data;
        }

        /// <summary>The decoded body.</summary>
        public T Data { get; }
    }
}
