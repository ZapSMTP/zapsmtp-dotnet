using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ZapSMTP.Tests
{
    /// <summary>The README's code, compiled so that it stays true; the API calls run against a fake handler.</summary>
    public sealed class ReadmeSamples
    {
        [Fact]
        public async Task The_README_samples_compile_and_run()
        {
            var h = new FakeHandler().Answer(() => Http.Json(200, new { id = "x", data = Array.Empty<object>(), has_more = false, errors = Array.Empty<object>() }));
            var zap = Clients.Make(h);
            var logoBytes = new byte[] { 1 };
            var email = new SendEmailParams { From = "a@x.test", To = new[] { "b@y.test" }, Subject = "s", Text = "t" };
            var emails = new[] { email };
            var id = "e1";
            var orderId = 7;

            var sent = await zap.Emails.SendAsync(new SendEmailParams
            {
                From = "Acme <hello@acme.com>",
                To = new[] { "jane@example.org" },
                Subject = "Your receipt",
                Html = "<p>Thanks for your order.</p>",
                Tags = new[] { new Tag { Name = "category", Value = "receipt" } },
                Metadata = new Dictionary<string, string> { ["order_id"] = "A-1042" },
            });
            Console.WriteLine(sent.Id);

            await zap.Emails.SendAsync(new SendEmailParams
            {
                From = "Acme <hello@acme.com>",
                To = new[] { "jane@example.org" },
                Template = new TemplateRef
                {
                    Id = "receipt",
                    Variables = new Dictionary<string, object?> { ["name"] = "Jane", ["total"] = 42 },
                },
            });

            await zap.Emails.SendAsync(new SendEmailParams
            {
                From = "Acme <hello@acme.com>",
                To = new[] { "jane@example.org" },
                Subject = "Your invoice",
                Text = "Attached.",
                Attachments = new[]
                {
                    OutgoingAttachment.FromText("invoice.txt", "total: 42"),
                    OutgoingAttachment.FromBytes("logo.png", logoBytes, contentId: "logo"),
                },
            });

            var batch = await zap.Emails.SendBatchAsync(emails, BatchValidation.Permissive);
            foreach (var error in batch.Errors ?? Array.Empty<SendBatchResponseError>())
                Console.WriteLine($"email {error.Index}: {error.Code} {error.Message}");

            var template = await zap.Templates.CreateAsync(new TemplatesCreateParams
            {
                Name = "Receipt",
                Subject = "Thanks, {{name}}",
                Html = "<p>Hello {{name}}, your total is {{total}}.</p>",
            });
            await zap.Templates.PublishAsync(template.Id!);
            var preview = await zap.Templates.RenderAsync(template.Id!, new TemplatesRenderParams
            {
                Variables = new Dictionary<string, object?> { ["name"] = "Jane", ["total"] = 42 },
            });
            var versions = await zap.Templates.Versions.ListAsync(template.Id!);
            Assert.NotNull(preview);
            Assert.NotNull(versions);

            Page<Email>? page = await zap.Emails.ListAsync(new EmailsListParams { Limit = 50, Status = EmailStatus.Bounced });
            if (page.HasNextPage) page = await page.GetNextPageAsync();
            await foreach (var e in zap.Emails.ListAutoPagingAsync(new EmailsListParams { Status = EmailStatus.Bounced }))
            {
                Console.WriteLine($"{e.Id} {string.Join(", ", e.To ?? Array.Empty<string>())}");
            }

            try
            {
                await zap.Emails.SendAsync(email);
            }
            catch (ValidationException e)
            {
                Console.Error.WriteLine($"{e.Code}: {e.Message}");
            }
            catch (RateLimitException e)
            {
                Console.Error.WriteLine($"retry in {e.RetryAfter}");
            }

            var tuned = new ZapSMTPClient("zap_...", new ZapSMTPClientOptions { MaxRetries = 4, Timeout = TimeSpan.FromSeconds(20) });
            Assert.Equal(4, tuned.MaxRetries);
            await zap.Emails.GetAsync(id, new RequestOptions { Timeout = TimeSpan.FromSeconds(5), MaxRetries = 0 });
            await zap.Emails.SendAsync(email, new RequestOptions { IdempotencyKey = $"order-{orderId}" });

            var response = await zap.Emails.SendWithResponseAsync(email);
            Console.WriteLine($"{response.Data.Id} {response.RequestId} {response.Headers.ContainsKey("RateLimit-Remaining")}");

            var services = new ServiceCollection();
            services.AddZapSMTP(o => o.ApiKey = "zap_...");
        }
    }
}
