using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ZapSMTP.Internal;

namespace ZapSMTP
{
    /// <summary>
    /// One page of a list. <see cref="GetNextPageAsync"/> fetches the next one; the services'
    /// <c>ListAutoPagingAsync</c> methods walk every item of every page.
    /// </summary>
    /// <typeparam name="T">The items' model.</typeparam>
    public sealed class Page<T> where T : class
    {
        private static readonly IReadOnlyDictionary<string, JsonElement> NoFields = new Dictionary<string, JsonElement>();

        private readonly ApiRequest? _next;
        private readonly Func<ApiRequest, CancellationToken, Task<Page<T>>>? _load;

        private Page(IReadOnlyList<T> data, bool hasMore, string? nextCursor, string? @object,
            IReadOnlyDictionary<string, JsonElement> additionalProperties, ApiRequest? next, Func<ApiRequest, CancellationToken, Task<Page<T>>>? load)
        {
            Data = data;
            HasMore = hasMore;
            NextCursor = nextCursor;
            Object = @object;
            AdditionalProperties = additionalProperties;
            _next = next;
            _load = load;
        }

        /// <summary>A page holding <paramref name="data"/>, with no next page: for tests and mocks.</summary>
        /// <param name="data">The items.</param>
        public Page(IReadOnlyList<T> data)
            : this(data ?? throw new ArgumentNullException(nameof(data)), false, null, "list", NoFields, null, null)
        {
        }

        /// <summary>The items of this page.</summary>
        public IReadOnlyList<T> Data { get; }

        /// <summary>Whether the API has more items after this page.</summary>
        public bool HasMore { get; }

        /// <summary>The cursor of the next page, for lists paged by cursor (suppressions).</summary>
        public string? NextCursor { get; }

        /// <summary>The object type, "list".</summary>
        public string? Object { get; }

        /// <summary>The page's other fields, as raw JSON.</summary>
        public IReadOnlyDictionary<string, JsonElement> AdditionalProperties { get; }

        /// <summary>Whether there is a next page to ask for. It makes no request.</summary>
        public bool HasNextPage => _next is not null;

        /// <summary>The next page, or null after the last one.</summary>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>The next page, or null.</returns>
        public async Task<Page<T>?> GetNextPageAsync(CancellationToken cancellationToken = default) =>
            _next is null ? null : await _load!(_next, cancellationToken).ConfigureAwait(false);

        internal static Page<T> Parse(string text, ApiRequest request, Paging paging, Func<ApiRequest, CancellationToken, Task<Page<T>>> load)
        {
            var root = ZapJson.TryParse(text);
            var raw = new List<JsonElement>();
            var data = new List<T>();
            var fields = new Dictionary<string, JsonElement>();
            var hasMore = false;
            string? nextCursor = null, @object = null;
            if (root is { ValueKind: JsonValueKind.Object } body)
            {
                foreach (var p in body.EnumerateObject())
                {
                    switch (p.Name)
                    {
                        case "data" when p.Value.ValueKind == JsonValueKind.Array:
                            raw.AddRange(p.Value.EnumerateArray());
                            break;
                        case "has_more":
                            hasMore = p.Value.ValueKind == JsonValueKind.True;
                            break;
                        case "next_cursor":
                            nextCursor = ZapJson.String(body, "next_cursor");
                            break;
                        case "object":
                            @object = ZapJson.String(body, "object");
                            break;
                        default:
                            fields[p.Name] = p.Value;
                            break;
                    }
                }
                foreach (var item in raw)
                {
                    if (item.ValueKind == JsonValueKind.Object) data.Add(item.Deserialize<T>(ZapJson.Options)!);
                }
            }
            var next = hasMore ? NextRequest(request, paging, raw, nextCursor) : null;
            return new Page<T>(data, hasMore, nextCursor, @object, fields, next, load);
        }

        // mirrors nextPage() in sdks/node/src/core.ts
        private static ApiRequest? NextRequest(ApiRequest request, Paging paging, List<JsonElement> raw, string? nextCursor)
        {
            var query = request.Query ?? new QueryString();
            JsonElement? first = raw.Count > 0 ? raw[0] : null;
            JsonElement? last = raw.Count > 0 ? raw[raw.Count - 1] : null;
            var lastId = NonEmpty(ZapJson.String(last, "id"));
            switch (paging)
            {
                case Paging.Cursor:
                    return NonEmpty(nextCursor) is { } cursor ? request.WithQuery(query.With("after", cursor)) : null;
                case Paging.Before:
                    return lastId is null ? null : request.WithQuery(query.With("before", lastId));
                case Paging.After:
                    return lastId is null ? null : request.WithQuery(query.With("after", lastId));
                case Paging.Logs:
                    // with after, logs come oldest-first from the server and newest-first in the page:
                    // the next newer page starts after the page's newest entry
                    if (query.Has("after"))
                        return NonEmpty(ZapJson.String(first, "id")) is { } firstId ? request.WithQuery(query.With("after", firstId)) : null;
                    return lastId is null ? null : request.WithQuery(query.With("before", lastId));
                case Paging.Thread:
                    var at = NonEmpty(ZapJson.String(last, "last_message_at"));
                    return lastId is null || at is null ? null : request.WithQuery(query.With("before", at).With("before_id", lastId));
                default:
                    return null;
            }
        }

        private static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
    }
}
