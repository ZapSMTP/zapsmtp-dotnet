using System.Collections.Generic;
using System.Text.Json;

namespace ZapSMTP.Internal
{
    /// <summary>What an error answer said.</summary>
    internal sealed class ApiError
    {
        private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

        public ApiError(int statusCode, string code, string type, string? requestId, IReadOnlyDictionary<string, string> headers, JsonElement? body, string? rawBody)
        {
            StatusCode = statusCode;
            Code = code;
            Type = type;
            RequestId = requestId;
            Headers = headers;
            Body = body;
            RawBody = rawBody;
        }

        public int StatusCode { get; }
        public string Code { get; }
        public string Type { get; }
        public string? RequestId { get; }
        public IReadOnlyDictionary<string, string> Headers { get; }
        public JsonElement? Body { get; }
        public string? RawBody { get; }

        /// <summary>A request the SDK refuses to send (status 0, code invalid_parameter).</summary>
        public static ValidationException Invalid(string message) =>
            new(message, new ApiError(0, "invalid_parameter", "invalid_request_error", null, NoHeaders, null, null));

        /// <summary>The exception for an error answer, by status.</summary>
        public static ZapSMTPException ToException(int status, IReadOnlyDictionary<string, string> headers, string text)
        {
            var body = ZapJson.TryParse(text);
            var error = ZapJson.Object(body, "error");
            var code = ZapJson.String(error, "code") ?? ZapJson.String(body, "name") ?? string.Empty;
            var message = ZapJson.String(error, "message") ?? ZapJson.String(body, "message") ?? $"HTTP {status}";
            headers.TryGetValue("x-request-id", out var requestId);
            var e = new ApiError(status, code, ZapJson.String(error, "type") ?? string.Empty, requestId, headers, body, text);
            return status switch
            {
                401 => new AuthenticationException(message, e),
                403 => new PermissionException(message, e),
                404 => new NotFoundException(message, e),
                409 => new ConflictException(message, e),
                400 or 413 or 422 => new ValidationException(message, e),
                429 => new RateLimitException(message, e),
                >= 500 => new ServerException(message, e),
                _ => new ZapSMTPException(message, e),
            };
        }
    }
}
