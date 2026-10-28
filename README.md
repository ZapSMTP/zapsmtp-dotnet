# ZapSMTP .NET SDK

The official .NET library for the [ZapSMTP](https://zapsmtp.com) email API: send, receive and track
email, manage templates, domains, webhooks and everything else the API does.

- Every API operation, with typed parameters and responses
- Safe automatic retries: sends carry an `Idempotency-Key`, so a retry never sends twice
- `await foreach` over every page of any list
- Webhook signature verification
- `IHttpClientFactory` and dependency injection
- .NET 8+, and anything that runs .NET Standard 2.0 (.NET Framework 4.6.2+, .NET 6, Unity); C# 9 or newer

## Install

```bash
dotnet add package ZapSMTP
```

## Send an email

```csharp
using ZapSMTP;

var zap = new ZapSMTPClient("zap_..."); // or set ZAP_API_KEY and call new ZapSMTPClient()

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
```

Make one client and keep it (register it as a singleton, or use [dependency injection](#dependency-injection)):
it is thread-safe and reuses its connections.

With a template:

```csharp
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
```

Attachments:

```csharp
await zap.Emails.SendAsync(new SendEmailParams
{
    From = "Acme <hello@acme.com>",
    To = new[] { "jane@example.org" },
    Subject = "Your invoice",
    Text = "Attached.",
    Attachments = new[]
    {
        await OutgoingAttachment.FromFileAsync("invoice.pdf"),
        OutgoingAttachment.FromBytes("logo.png", logoBytes, contentId: "logo"), // <img src="cid:logo">
    },
});
```

The content type follows the file name's extension unless you pass `contentType`.

Up to 100 emails at once:

```csharp
var batch = await zap.Emails.SendBatchAsync(emails, BatchValidation.Permissive);
foreach (var error in batch.Errors ?? Array.Empty<SendBatchResponseError>())
    Console.WriteLine($"email {error.Index}: {error.Code} {error.Message}");
```

## Templates

```csharp
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
```

## Lists

A `ListAsync` method returns one page; `ListAutoPagingAsync` walks every item of every page, keeping
your filters.

```csharp
var page = await zap.Emails.ListAsync(new EmailsListParams { Limit = 50, Status = EmailStatus.Bounced });
if (page.HasNextPage) page = await page.GetNextPageAsync();

await foreach (var email in zap.Emails.ListAutoPagingAsync(new EmailsListParams { Status = EmailStatus.Bounced }))
{
    Console.WriteLine($"{email.Id} {string.Join(", ", email.To ?? Array.Empty<string>())}");
}
```

`HasNextPage` never makes a request; `GetNextPageAsync` returns null after the last page.

## Responses

Response properties are nullable: the SDK decodes leniently and never fails on a field of an
unexpected type, which is left null. Fields this version does not know are kept in
`AdditionalProperties` as raw JSON. Times are `DateTimeOffset`, counts `long`, rates `double`, and
values from a fixed set are strings with constants: `EmailStatus`, `EmailStream`, `WebhookEventType`,
`SuppressionReason` and others.

To send a field the SDK does not know yet, add it to any parameters' `AdditionalProperties`. A
nullable field such as `ApiKeysUpdateParams.DomainId` is sent as `null` when you set it to `null`, and
left out when you do not set it. A list or dictionary you set to empty is sent empty.

## Errors

Every API error throws a `ZapSMTPException` subclass with `StatusCode`, `Code`, `Message`, `Type`,
`RequestId`, `Headers` and `Body`.

```csharp
try
{
    await zap.Emails.SendAsync(email);
}
catch (ValidationException e)
{
    Console.Error.WriteLine($"{e.Code}: {e.Message}"); // e.g. invalid_parameter
}
catch (RateLimitException e)
{
    Console.Error.WriteLine($"retry in {e.RetryAfter}");
}
```

| Class | When |
|---|---|
| `AuthenticationException` | 401: missing or invalid key |
| `PermissionException` | 403: the key may not do this, or the domain is not verified |
| `NotFoundException` | 404 |
| `ConflictException` | 409 |
| `ValidationException` | 400, 413, 422: the request is wrong; also an id that is empty, `.` or `..`, refused before sending |
| `RateLimitException` | 429: `RetryAfter` says how long to wait |
| `ServerException` | 5xx |
| `ConnectionException` | no answer, or one that broke off: network failure or timeout |

Cancelling the `CancellationToken` you pass throws `OperationCanceledException`, as usual in .NET.

## Retries and timeouts

A request that failed on the way (429, 408, 5xx, a lost connection) is tried again twice by default,
waiting as the API's `Retry-After` says or backing off exponentially. Only requests that are safe to
repeat are retried: reads, updates by `PUT`, deletes, and sends, which always carry an
`Idempotency-Key` (pass your own with `RequestOptions.IdempotencyKey` to make your own retries safe
too; an empty one is refused, since it would turn deduplication off). The timeout covers each attempt, reading the answer included.

```csharp
var zap = new ZapSMTPClient("zap_...", new ZapSMTPClientOptions { MaxRetries = 4, Timeout = TimeSpan.FromSeconds(20) });
await zap.Emails.GetAsync(id, new RequestOptions { Timeout = TimeSpan.FromSeconds(5), MaxRetries = 0 });
await zap.Emails.SendAsync(email, new RequestOptions { IdempotencyKey = $"order-{orderId}" });
```

## Response headers

Every method has a `...WithResponseAsync` twin that also gives the status, headers and request id:

```csharp
var response = await zap.Emails.SendWithResponseAsync(email);
Console.WriteLine($"{response.Data.Id} {response.RequestId} {response.Headers["RateLimit-Remaining"]}");
```

## Webhooks

Check the signature on the raw body, before parsing it. With ASP.NET Core minimal APIs:

```csharp
app.MapPost("/webhooks/zapsmtp", async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();
    try
    {
        var evt = Webhooks.Verify(body, request.Headers["Zap-Signature"].ToString(), secret);
        if (evt.Type == WebhookEventType.EmailBounced) { /* evt.Data is the event's JSON */ }
        return Results.Ok();
    }
    catch (WebhookVerificationException)
    {
        return Results.Unauthorized();
    }
});
```

`Webhooks.Verify` throws `WebhookVerificationException` when the signature is missing, wrong or older
than five minutes. A webhook with batch delivery (`Batch = true`) sends an array of events: read it
with `Webhooks.VerifyEvents`, which returns every event (and one event as a list of one).
`evt.GetData<T>()` decodes an event's data into a type of your own.

## Dependency injection

```bash
dotnet add package ZapSMTP.Extensions.DependencyInjection
```

```csharp
builder.Services.AddZapSMTP(o => o.ApiKey = builder.Configuration["ZapSMTP:ApiKey"]);

// then inject ZapSMTPClient anywhere
app.MapPost("/signup", async (ZapSMTPClient zap) => { /* ... */ });
```

The client is a typed client of `IHttpClientFactory`. `AddZapSMTP` returns the `IHttpClientBuilder`,
to add handlers or configure the primary one.

## Configuration

| Option (`ZapSMTPClientOptions`) | Environment | Default |
|---|---|---|
| `ApiKey` | `ZAP_API_KEY` | required |
| `BaseUrl` | `ZAP_BASE_URL` | `https://api.zapsmtp.com/v1` |
| `Timeout` | | 60 s per attempt |
| `MaxRetries` | | 2 |
| `DefaultHeaders` | | |
| `HttpClient` | | one the client makes and disposes |

## Reference

Every method is listed with its parameters in the [API reference](https://zapsmtp.com/docs/).
The SDK's services mirror it: `zap.Emails`, `zap.Emails.Receiving`, `zap.Threads`, `zap.Domains`,
`zap.Webhooks`, `zap.Actions`, `zap.Templates`, `zap.Variables`, `zap.TemplateModules`,
`zap.TemplateAssets`, `zap.TemplateTimers`, `zap.Inbound`, `zap.Suppressions`, `zap.Stats`,
`zap.ApiKeys`, `zap.SmtpCredentials`, `zap.Logs`, `zap.Usage`, `zap.Simulator`, `zap.DedicatedIps`.
Service methods are virtual, so they can be mocked in your tests.

## Development

The services, parameters and models in `src/ZapSMTP/Generated` are generated from the API reference
by `sdks/codegen` (`node sdks/codegen/generate.mjs --only dotnet`); change the generator, never those
files.

```bash
dotnet test tests/ZapSMTP.Tests                              # the net8.0 build; coverage must be 100%
dotnet test tests/ZapSMTP.Tests -p:ZapLibTfm=netstandard2.0  # the netstandard2.0 build
dotnet test tests/ZapSMTP.Tests -p:TestTargetFramework=net9.0
```

The tests run every API operation against the conformance server (`sdks/conformance`, started with
`node` unless `ZAP_CONFORMANCE_URL` points at one), the core tests and the shared webhook vectors.
