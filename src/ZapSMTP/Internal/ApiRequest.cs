using System.Collections.Generic;

namespace ZapSMTP.Internal
{
    /// <summary>How a list asks for its next page (sdks/spec/names.json paging).</summary>
    internal enum Paging
    {
        /// <summary>One page only.</summary>
        None,

        /// <summary>before = the last item's id.</summary>
        Before,

        /// <summary>after = the last item's id.</summary>
        After,

        /// <summary>after = the first item's id when the caller paged with after, else before = the last item's id.</summary>
        Logs,

        /// <summary>before = the last item's last_message_at and before_id = its id.</summary>
        Thread,

        /// <summary>after = next_cursor.</summary>
        Cursor,
    }

    /// <summary>One API call, as the generated services describe it.</summary>
    internal sealed class ApiRequest
    {
        public ApiRequest(string method, string path)
        {
            Method = method;
            Path = path;
        }

        public string Method { get; }

        public string Path { get; }

        public QueryString? Query { get; init; }

        /// <summary>Sent as JSON.</summary>
        public object? Body { get; init; }

        /// <summary>Sent as multipart/form-data instead of JSON.</summary>
        public MultipartForm? Form { get; init; }

        public IReadOnlyDictionary<string, string>? Headers { get; init; }

        /// <summary>The operation takes Idempotency-Key, so the SDK always sends one and may retry.</summary>
        public bool Idempotent { get; init; }

        public ApiRequest WithQuery(QueryString query) =>
            new(Method, Path) { Query = query, Body = Body, Form = Form, Headers = Headers, Idempotent = Idempotent };
    }
}
