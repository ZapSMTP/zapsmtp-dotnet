using System;
using System.Collections.Generic;
using System.Text.Json;
using ZapSMTP.Internal;

namespace ZapSMTP
{
    /// <summary>An error answer of the API, or no answer at all (<see cref="ConnectionException"/>).</summary>
    public class ZapSMTPException : Exception
    {
        private static readonly IReadOnlyDictionary<string, string> NoHeaders =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>An error with a message only.</summary>
        /// <param name="message">What went wrong.</param>
        public ZapSMTPException(string message)
            : this(message, (Exception?)null)
        {
        }

        /// <summary>An error caused by another one.</summary>
        /// <param name="message">What went wrong.</param>
        /// <param name="innerException">The cause.</param>
        public ZapSMTPException(string message, Exception? innerException)
            : base(message, innerException)
        {
            Code = string.Empty;
            Type = string.Empty;
            Headers = NoHeaders;
        }

        internal ZapSMTPException(string message, ApiError error, Exception? innerException = null)
            : base(message, innerException)
        {
            StatusCode = error.StatusCode;
            Code = error.Code;
            Type = error.Type;
            RequestId = error.RequestId;
            Headers = error.Headers;
            Body = error.Body;
            RawBody = error.RawBody;
        }

        /// <summary>The HTTP status; 0 when there was no answer.</summary>
        public int StatusCode { get; }

        /// <summary>The API's error code, such as <c>invalid_parameter</c>; empty when it gave none.</summary>
        public string Code { get; }

        /// <summary>The error type, such as <c>invalid_request_error</c>; empty when it gave none.</summary>
        public string Type { get; }

        /// <summary>The X-Request-Id of the answer: quote it to support.</summary>
        public string? RequestId { get; }

        /// <summary>The answer's headers (names are case-insensitive).</summary>
        public IReadOnlyDictionary<string, string> Headers { get; }

        /// <summary>The answer's body, when it was JSON.</summary>
        public JsonElement? Body { get; }

        /// <summary>The answer's body as text.</summary>
        public string? RawBody { get; }
    }

    /// <summary>401: the API key is missing or invalid.</summary>
    public sealed class AuthenticationException : ZapSMTPException
    {
        /// <summary>An error with a message only.</summary>
        /// <param name="message">What went wrong.</param>
        public AuthenticationException(string message) : base(message) { }

        internal AuthenticationException(string message, ApiError error) : base(message, error) { }
    }

    /// <summary>403: the key may not do this, or the domain is not verified.</summary>
    public sealed class PermissionException : ZapSMTPException
    {
        /// <summary>An error with a message only.</summary>
        /// <param name="message">What went wrong.</param>
        public PermissionException(string message) : base(message) { }

        internal PermissionException(string message, ApiError error) : base(message, error) { }
    }

    /// <summary>404: no such object.</summary>
    public sealed class NotFoundException : ZapSMTPException
    {
        /// <summary>An error with a message only.</summary>
        /// <param name="message">What went wrong.</param>
        public NotFoundException(string message) : base(message) { }

        internal NotFoundException(string message, ApiError error) : base(message, error) { }
    }

    /// <summary>409: the request conflicts with the state of an object, or an idempotency key was reused.</summary>
    public sealed class ConflictException : ZapSMTPException
    {
        /// <summary>An error with a message only.</summary>
        /// <param name="message">What went wrong.</param>
        public ConflictException(string message) : base(message) { }

        internal ConflictException(string message, ApiError error) : base(message, error) { }
    }

    /// <summary>400, 413 or 422: the request is wrong.</summary>
    public sealed class ValidationException : ZapSMTPException
    {
        /// <summary>An error with a message only.</summary>
        /// <param name="message">What went wrong.</param>
        public ValidationException(string message) : base(message) { }

        internal ValidationException(string message, ApiError error) : base(message, error) { }
    }

    /// <summary>429: too many requests, or a quota is used up.</summary>
    public sealed class RateLimitException : ZapSMTPException
    {
        /// <summary>An error with a message only.</summary>
        /// <param name="message">What went wrong.</param>
        public RateLimitException(string message) : base(message) { }

        internal RateLimitException(string message, ApiError error) : base(message, error) { }

        /// <summary>How long the API asked to wait (Retry-After), when it said; an absurd value is capped at a day.</summary>
        public TimeSpan? RetryAfter =>
            RetryPolicy.RetryAfterSeconds(Headers, DateTimeOffset.UtcNow) is { } seconds ? TimeSpan.FromSeconds(Math.Min(seconds, MaxRetryAfterSeconds)) : null;

        private const double MaxRetryAfterSeconds = 86400;
    }

    /// <summary>5xx: the API failed.</summary>
    public sealed class ServerException : ZapSMTPException
    {
        /// <summary>An error with a message only.</summary>
        /// <param name="message">What went wrong.</param>
        public ServerException(string message) : base(message) { }

        internal ServerException(string message, ApiError error) : base(message, error) { }
    }

    /// <summary>
    /// No answer, or one that broke off: a network failure or a timeout. <see cref="ZapSMTPException.StatusCode"/>
    /// is 0, unless the answer's status and headers arrived before its body broke off: they are then kept, with
    /// its request id.
    /// </summary>
    public sealed class ConnectionException : ZapSMTPException
    {
        /// <summary>An error caused by another one.</summary>
        /// <param name="message">What went wrong.</param>
        /// <param name="innerException">The cause.</param>
        public ConnectionException(string message, Exception? innerException = null) : base(message, innerException) { }

        internal ConnectionException(string message, Exception innerException, ZapResponse response)
            : base(message, new ApiError(response.StatusCode, string.Empty, string.Empty, response.RequestId, response.Headers, null, null), innerException)
        {
        }
    }

    /// <summary>A webhook request's signature is missing, wrong or too old, or its body is not JSON.</summary>
    public sealed class WebhookVerificationException : ZapSMTPException
    {
        /// <summary>An error caused by another one.</summary>
        /// <param name="message">What went wrong.</param>
        /// <param name="innerException">The cause.</param>
        public WebhookVerificationException(string message, Exception? innerException = null) : base(message, innerException) { }
    }
}
