using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ZapSMTP.Tests
{
    public sealed class codexSdkdotnetPagingTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task TestCodexSdkdotnetCancellationStopsInsideAnAlreadyLoadedPage(bool hasMore)
        {
            var h = new FakeHandler().Answer(() => Http.List(new object[]
            {
                new { id = "first" }, new { id = "second" }, new { id = "third" },
            }, hasMore));
            using var zap = Clients.Make(h);
            using var cts = new CancellationTokenSource();
            var seen = new List<string>();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (var email in zap.Emails.ListAutoPagingAsync().WithCancellation(cts.Token))
                {
                    seen.Add(email.Id!);
                    cts.Cancel();
                }
            });

            Assert.Equal(new[] { "first" }, seen);
            Assert.Single(h.Calls);
        }
    }
}
