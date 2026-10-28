using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ZapSMTP.Tests
{
    public sealed class PagingTests
    {
        private static object[] Ids(params string[] ids) => ids.Select(id => (object)new { id }).ToArray();

        private static Dictionary<string, string> Query(Call call) =>
            new Uri(call.Url).Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=')).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));

        [Fact]
        public async Task Walks_every_page_with_before_the_last_id_keeping_the_filters()
        {
            var h = new FakeHandler().Answer(() => Http.List(Ids("a", "b"))).Answer(() => Http.List(Ids("c"), false));
            var ids = new List<string>();
            await foreach (var e in Clients.Make(h).Emails.ListAutoPagingAsync(new EmailsListParams { Limit = 2, After = "2026-01-01T00:00:00Z" })) ids.Add(e.Id!);
            Assert.Equal(new[] { "a", "b", "c" }, ids);
            Assert.Equal("https://api.test/v1/emails?limit=2&after=2026-01-01T00%3A00%3A00Z&before=b", h.Calls[1].Url);
        }

        [Fact]
        public async Task Replaces_a_before_the_caller_passed()
        {
            var h = new FakeHandler().Answer(() => Http.List(Ids("a")));
            var page = await Clients.Make(h).Emails.Receiving.ListAsync(new EmailsReceivingListParams { Before = "z", Limit = 1 });
            await page.GetNextPageAsync();
            Assert.Equal("?limit=1&before=a", h.Calls[1].Query);
        }

        [Fact]
        public async Task Walks_suppressions_with_next_cursor()
        {
            var h = new FakeHandler()
                .Answer(() => Http.List(new object[] { new { email = "a@x.test" } }, true, new { next_cursor = "cur1" }))
                .Answer(() => Http.List(new object[] { new { email = "b@x.test" } }, true, new { next_cursor = (string?)null }));
            var emails = new List<string>();
            await foreach (var s in Clients.Make(h).Suppressions.ListAutoPagingAsync()) emails.Add(s.Email!);
            Assert.Equal(new[] { "a@x.test", "b@x.test" }, emails);
            Assert.Equal("https://api.test/v1/suppressions?after=cur1", h.Calls[1].Url);
            Assert.Equal(2, h.Calls.Count);
        }

        [Fact]
        public async Task Gives_one_page_when_awaited()
        {
            var h = new FakeHandler().Answer(() => Http.List(Ids("a"), true, new { total = 3 }));
            var first = await Clients.Make(h).Emails.ListAsync();
            Assert.Equal("a", Assert.Single(first.Data).Id);
            Assert.True(first.HasMore);
            Assert.True(first.HasNextPage);
            Assert.Equal("list", first.Object);
            Assert.Null(first.NextCursor);
            Assert.Equal(3, first.AdditionalProperties["total"].GetInt32());
            var res = await Clients.Make(h).Emails.ListWithResponseAsync();
            Assert.Equal(200, res.StatusCode);
            Assert.Single(res.Data.Data);
        }

        [Fact]
        public async Task HasNextPage_only_inspects_the_page()
        {
            var h = new FakeHandler().Answer(() => Http.List(Ids("email-1")));
            var page = await Clients.Make(h).Emails.ListAsync();
            Assert.True(page.HasNextPage);
            Assert.True(page.HasNextPage);
            Assert.Single(h.Calls);
        }

        [Fact]
        public async Task Stops_where_no_next_page_can_be_asked_for()
        {
            var noId = new FakeHandler().Answer(() => Http.Json(200, new { @object = "list", has_more = true, data = new object[] { new { }, "junk" } }));
            var p = await Clients.Make(noId).Emails.ListAsync();
            Assert.False(p.HasNextPage);
            Assert.Null(await p.GetNextPageAsync());
            Assert.Single(p.Data); // the string is not an email
            var unpaged = new FakeHandler().Answer(() => Http.List(Ids("a")));
            var d = await Clients.Make(unpaged).Domains.ListAsync();
            Assert.False(d.HasNextPage);
            Assert.Null(await d.GetNextPageAsync());
            var last = new FakeHandler().Answer(() => Http.List(Ids("a"), false));
            Assert.Null(await (await Clients.Make(last).Emails.ListAsync()).GetNextPageAsync());
            var empty = new FakeHandler().Answer(() => Http.Json(200, new { @object = "list" }));
            var e = await Clients.Make(empty).Emails.ListAsync();
            Assert.Empty(e.Data);
            Assert.False(e.HasMore);
            var odd = new FakeHandler().Answer(() => Http.Json(200, new { @object = 1, data = "x", has_more = "yes", next_cursor = 2 }));
            var o = await Clients.Make(odd).Suppressions.ListAsync();
            Assert.Empty(o.Data);
            Assert.False(o.HasMore);
            Assert.Null(o.Object);
            Assert.Null(o.NextCursor);
            Assert.Equal(JsonValueKind.String, o.AdditionalProperties["data"].ValueKind);
            var notJson = new FakeHandler().Answer(() => Http.Text(200, "oops"));
            Assert.Empty((await Clients.Make(notJson).Emails.ListAsync()).Data);
            var emptyCursor = new FakeHandler().Answer(() => Http.List(Ids("a"), true, new { next_cursor = "" }));
            Assert.False((await Clients.Make(emptyCursor).Suppressions.ListAsync()).HasNextPage);
        }

        [Fact]
        public async Task Conversation_paging_uses_the_last_activity_time_and_the_tie_breaking_thread_id()
        {
            var last = new { id = "019aaa00-0000-7000-8000-000000000001", last_message_at = "2026-10-28T08:00:00Z" };
            var h = new FakeHandler().Answer(() => Http.List(new object[] { last }));
            var page = await Clients.Make(h).Threads.ListAsync(new ThreadsListParams { Limit = 1, Unread = true });
            await page.GetNextPageAsync();
            var q = Query(h.Calls[1]);
            Assert.Equal(last.last_message_at, q["before"]);
            Assert.Equal(last.id, q["before_id"]);
            Assert.Equal("true", q["unread"]);
            var bare = new FakeHandler().Answer(() => Http.List(Ids("x")));
            Assert.False((await Clients.Make(bare).Threads.ListAsync()).HasNextPage); // no last_message_at: nothing to ask for
            var noId = new FakeHandler().Answer(() => Http.List(new object[] { new { last_message_at = "2026-10-28T08:00:00Z" } }));
            Assert.False((await Clients.Make(noId).Threads.ListAsync()).HasNextPage);
        }

        [Fact]
        public async Task Received_attachments_page_forward_after_the_last_id()
        {
            var h = new FakeHandler().Answer(() => Http.List(Ids("a1", "a2")));
            var page = await Clients.Make(h).Emails.Receiving.Attachments.ListAsync("m1", new EmailsReceivingAttachmentsListParams { Limit = 2 });
            await page.GetNextPageAsync();
            Assert.Equal("a2", Query(h.Calls[1])["after"]);
            var empty = new FakeHandler().Answer(() => Http.List(Array.Empty<object>()));
            Assert.False((await Clients.Make(empty).Emails.Receiving.Attachments.ListAsync("m1")).HasNextPage);
        }

        [Fact]
        public async Task Logs_page_back_with_before_or_forward_after_the_newest_entry_when_paged_with_after()
        {
            var back = new FakeHandler().Answer(() => Http.List(Ids("l3", "l2")));
            await (await Clients.Make(back).Logs.ListAsync()).GetNextPageAsync();
            Assert.Equal("l2", Query(back.Calls[1])["before"]);
            var fwd = new FakeHandler().Answer(() => Http.List(Ids("l9", "l8")));
            await (await Clients.Make(fwd).Logs.ListAsync(new LogsListParams { After = "l7" })).GetNextPageAsync();
            var q = Query(fwd.Calls[1]);
            Assert.Equal("l9", q["after"]);
            Assert.False(q.ContainsKey("before"));
            var empty = new FakeHandler().Answer(() => Http.List(Array.Empty<object>()));
            Assert.False((await Clients.Make(empty).Logs.ListAsync(new LogsListParams { After = "l7" })).HasNextPage);
            Assert.False((await Clients.Make(empty).Logs.ListAsync()).HasNextPage);
            Assert.False((await Clients.Make(empty).Emails.Receiving.ListAsync()).HasNextPage);
        }

        [Fact]
        public async Task Auto_paging_passes_the_cancellation_token()
        {
            var h = new FakeHandler().Answer(() => Http.List(Ids("a"))).Answer(() => Http.List(Ids("b")));
            using var cts = new CancellationTokenSource();
            var seen = new List<string>();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (var e in Clients.Make(h).Emails.ListAutoPagingAsync().WithCancellation(cts.Token))
                {
                    seen.Add(e.Id!);
                    cts.Cancel();
                }
            });
            Assert.Equal(new[] { "a" }, seen);
            Assert.Single(h.Calls);
        }

        [Fact]
        public void A_page_can_be_made_for_tests()
        {
            var page = new Page<Email>(new[] { new Email { Id = "e1" } });
            Assert.False(page.HasNextPage);
            Assert.Equal("list", page.Object);
            Assert.Empty(page.AdditionalProperties);
            Assert.Throws<ArgumentNullException>(() => new Page<Email>(null!));
        }
    }
}
