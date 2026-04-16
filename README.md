# Reflag .NET SDK

.NET server SDK for [Reflag](https://reflag.com).

## Installation

Install the `Reflag.RuntimeSDK` package:

```bash
dotnet add package Reflag.RuntimeSDK
```

Or add the package reference directly:

<!-- x-release-please-start-version -->
```xml
<ItemGroup>
  <PackageReference Include="Reflag.RuntimeSDK" Version="0.0.1" />
</ItemGroup>
```
<!-- x-release-please-end -->

## Basic usage

Set `REFLAG_SECRET_KEY` in your environment, then create one long-lived `ReflagClient`, initialize it once, and reuse it for flag evaluation and tracking.

```bash
export REFLAG_SECRET_KEY=your-secret-key
```

```csharp
using Reflag;

await using var client = new ReflagClient();
await client.InitializeAsync();

var context = ReflagContext.From(new
{
    User = new
    {
        Id = "user-123",
        Email = "ada@example.com",
    },
    Company = new
    {
        Id = "company-456",
        Name = "Acme",
        Plan = "enterprise",
    },
    Other = new
    {
        Device = "Desktop",
    },
});

var enabled = client.GetFlag("new-dashboard", context);

if (enabled)
{
    // flag-gated code
}
```

`ReflagContext.From(...)` accepts anonymous objects or nested dictionaries. Well-known fields like `Id`, `Name`, `Email`, and `Avatar` are mapped directly; additional fields become attributes.

If you evaluate multiple flags for the same context, for example in middleware or request-scoped helpers, bind that context once and reuse the bound client:

```csharp
var bound = client.BindClient(context);

var newDashboardEnabled = bound.GetFlag("new-dashboard");
var betaSearchEnabled = bound.GetFlag("beta-search");
```

In ASP.NET Core apps, prefer `AddReflag(...)` so initialization and shutdown flushing happen through the host lifecycle:

```csharp
using Reflag;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddReflag(new ReflagClientOptions());
```

## High performance flag targeting

`InitializeAsync()` fetches the current flag definitions from Reflag.
After that, `GetFlag(...)` and `GetFlagsForBootstrap(...)` evaluate flags locally against the in-memory cached definitions, so ordinary flag reads do not need a network round trip after initialization completes.

The SDK keeps definitions fresh in the background using push sync by default. You can switch to polling mode instead.
It also batches tracking-related events and applies internal dedupe/rate limiting to optimize tracking traffic.

## Fallback provider

`FlagsFallbackProvider` is a reliability feature for startup fallback and outage recovery.
It lets the SDK save the last successfully fetched flag definitions to durable storage, so a future process can still initialize if Reflag is temporarily unavailable during startup.

If you do not configure a fallback provider, the SDK still uses live definitions and keeps the latest successful copy in memory for the lifetime of the process. A fallback provider only adds durable storage for that snapshot.

Reflag remains the source of truth. In the normal case, the SDK fetches live definitions and then saves an updated snapshot through the fallback provider. The fallback copy is only used when that initial live fetch is unavailable.

Built-in providers currently available in the .NET package:

- `ReflagFallbackProviders.Static(...)`
- `ReflagFallbackProviders.File(...)`
- `ReflagFallbackProviders.AzureBlob(...)`
- `ReflagFallbackProviders.Redis(...)`
- `ReflagFallbackProviders.S3(...)`
- `ReflagFallbackProviders.Gcs(...)`

Static fallback example:

```csharp
var client = new ReflagClient(new ReflagClientOptions
{
    FlagsFallbackProvider = ReflagFallbackProviders.Static(new Dictionary<string, bool>
    {
        ["demo-flag"] = true,
        ["new-dashboard"] = false,
    }),
});
```

File fallback example:

```csharp
var client = new ReflagClient(new ReflagClientOptions
{
    FlagsFallbackProvider = ReflagFallbackProviders.File(new FileFallbackProviderOptions
    {
        Directory = Path.Combine(AppContext.BaseDirectory, ".reflag"),
    }),
});
```

## Bootstrapping client-side applications

If you are serializing flags into another runtime or handing them off to a client application, use `GetFlagsForBootstrap(...)`.
It returns JSON-serializable flag data together with the context used for evaluation, which bootstrap flows need.

```csharp
var bootstrapped = client.GetFlagsForBootstrap(new ReflagContext
{
    User = new ReflagUserContext
    {
        Id = "user-123",
        Name = "Ada",
        Email = "ada@example.com",
    },
    Company = new ReflagCompanyContext
    {
        Id = "company-456",
        Name = "Acme",
    },
    Other = new Dictionary<string, object?>
    {
        ["Device"] = "Desktop",
    },
});

var json = System.Text.Json.JsonSerializer.Serialize(bootstrapped);
```

Bound client usage is often a little cleaner:

```csharp
var bound = client.BindClient(new ReflagContext
{
    User = new ReflagUserContext { Id = "user-123" },
    Company = new ReflagCompanyContext { Id = "company-456" },
});

var bootstrapped = bound.GetFlagsForBootstrap();
```

## Remote config

Not supported at the moment.

## Configuring

The .NET SDK currently supports configuration through:

1. constructor options
2. environment variables

Constructor options take precedence over environment variables.

Supported environment variables today:

- `REFLAG_SECRET_KEY`
- `REFLAG_API_BASE_URL`
- `REFLAG_OFFLINE`
- `REFLAG_FLAGS_ENABLED`
- `REFLAG_FLAGS_DISABLED`

`REFLAG_FLAGS_ENABLED` and `REFLAG_FLAGS_DISABLED` are comma-separated lists.

Example:

```bash
export REFLAG_SECRET_KEY=your-secret-key
export REFLAG_API_BASE_URL=https://front.reflag.com
export REFLAG_FLAGS_ENABLED=demo-flag
export REFLAG_FLAGS_DISABLED=new-dashboard
```

Constructor options currently available:

| Option | Type | Notes | Env var |
| --- | --- | --- | --- |
| `SecretKey` | `string?` | Required unless `Offline == true` | `REFLAG_SECRET_KEY` |
| `ApiBaseUrl` | `Uri?` | Absolute URI | `REFLAG_API_BASE_URL` |
| `Logger` | `ILogger?` | Defaults to `NullLogger.Instance` | - |
| `HttpClient` | `HttpClient?` | Customize the built-in HTTP transport | - |
| `HttpTransport` | `IReflagHttpTransport?` | Replace the regular request transport with a custom implementation | - |
| `FlagsFallbackProvider` | `IFlagsFallbackProvider?` | Ignored in offline mode | - |
| `FetchTimeout` | `TimeSpan?` | Default `10s` | - |
| `FlagsFetchRetries` | `int?` | Default `3` | - |
| `Batch` | `ReflagBatchOptions?` | Max size, interval, shutdown flush | - |
| `FlagOverrides` | `IReadOnlyDictionary<string, bool>?` | Base local overrides | `REFLAG_FLAGS_ENABLED`, `REFLAG_FLAGS_DISABLED` |
| `FlagOverridesFactory` | `Func<ReflagContext, IReadOnlyDictionary<string, bool>>?` | Context-dependent base overrides | - |
| `Offline` | `bool?` | Disables network I/O | `REFLAG_OFFLINE` |
| `FlagsSyncMode` | `ReflagFlagsSyncMode?` | Default is `Push` | - |
| `FlagsPushUrl` | `Uri?` | Override SSE endpoint | - |

Most applications do not need both `HttpClient` and `HttpTransport`. Use `HttpClient` when you want to customize the SDK's built-in transport. Use `HttpTransport` when you want to replace the regular request transport entirely. If you provide `HttpTransport` and keep push sync enabled, `HttpClient` still configures the built-in SSE client used for push updates.

## Testing with flag overrides

The SDK has first-class override APIs for tests and local development.
In tests, you will often want to run the client in offline mode:

```csharp
await using var client = new ReflagClient(new ReflagClientOptions
{
    Offline = true,
});
```

### Base overrides

Set base overrides in the constructor, replace them later with `SetFlagOverrides(...)`, and clear them with `ClearFlagOverrides()`:

```csharp
await using var client = new ReflagClient(new ReflagClientOptions
{
    Offline = true,
    FlagOverrides = new Dictionary<string, bool>
    {
        ["demo-flag"] = true,
    },
});

client.SetFlagOverrides(new Dictionary<string, bool>
{
    ["demo-flag"] = false,
});

client.ClearFlagOverrides();
```

### Layering overrides

`PushFlagOverrides(...)` adds a temporary layer on top of the base overrides and returns an `IDisposable` scope.
The override is active until that scope is disposed.

```csharp
await using var client = new ReflagClient(new ReflagClientOptions
{
    Offline = true,
});

Assert.False(client.GetFlag("new-dashboard", new ReflagContext()));

using (client.PushFlagOverrides(new Dictionary<string, bool>
{
    ["new-dashboard"] = true,
}))
{
    Assert.True(client.GetFlag("new-dashboard", new ReflagContext()));
}

Assert.False(client.GetFlag("new-dashboard", new ReflagContext()));
```

The precedence is:

1. base overrides from the constructor or `SetFlagOverrides(...)`
2. temporary layers added by `PushFlagOverrides(...)`

### Context dependent overrides

`SetFlagOverrides(...)` and `PushFlagOverrides(...)` also accept a function:

```csharp
using (client.PushFlagOverrides(context => new Dictionary<string, bool>
{
    ["smart-summaries"] = context.User?.Id == "qa-user",
}))
{
    Assert.True(client.GetFlag(
        "smart-summaries",
        new ReflagContext
        {
            User = new ReflagUserContext { Id = "qa-user" },
        }));
}
```

### Additional ways to provide flag overrides

You can also provide overrides through environment variables:

```bash
export REFLAG_FLAGS_ENABLED=flag-a,flag-b
export REFLAG_FLAGS_DISABLED=flag-c
```

The example app tests in this repository also use `PushFlagOverrides(...)` to toggle flags through the real minimal API endpoints.
See:

- `examples/Reflag.MinimalApiExample.Tests/ExampleAppTests.cs`

## Using with ASP.NET Core

A natural .NET integration is registering the SDK in ASP.NET Core DI and resolving `ReflagClient` in endpoints or services:

```csharp
using Reflag;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddReflag(sp => new ReflagClientOptions
{
    SecretKey = builder.Configuration["REFLAG_SECRET_KEY"],
    Logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Reflag"),
});

var app = builder.Build();

app.MapGet("/flags/{key}", (string key, ReflagClient reflag) =>
{
    var enabled = reflag.GetFlag(key, new ReflagContext
    {
        User = new ReflagUserContext { Id = "user-123" },
    });

    return Results.Ok(new { key, enabled });
});
```

See also:

- `examples/Reflag.MinimalApiExample/Program.cs`
- `examples/Reflag.MinimalApiExample/ReflagMinimalApiExampleApp.cs`

## Disable tracking

Most applications should leave telemetry enabled.
Disabling it is mainly useful for cases where you are evaluating flags on behalf of another user or company without wanting that activity to be recorded in Reflag, such as support/admin impersonation flows, internal previews, or diagnostic tooling.

Use `ReflagTelemetryOptions` to disable automatic telemetry for a bound client or a specific read call.

```csharp
var bound = client.BindClient(
    new ReflagContext
    {
        User = new ReflagUserContext { Id = "user-123" },
        Company = new ReflagCompanyContext { Id = "company-456" },
    },
    new ReflagTelemetryOptions
    {
        EnableTelemetry = false,
    });

var enabled = bound.GetFlag("new-dashboard");
await bound.TrackAsync("opened-dashboard"); // no-op when telemetry is disabled
```

You can also pass telemetry options directly to `GetFlag(...)` or `GetFlagsForBootstrap(...)`:

```csharp
var enabled = client.GetFlag(
    "new-dashboard",
    new ReflagContext
    {
        User = new ReflagUserContext { Id = "user-123" },
    },
    new ReflagTelemetryOptions
    {
        EnableTelemetry = false,
    });
```

Calling `UpdateUserAsync(...)`, `UpdateCompanyAsync(...)`, or `TrackAsync(...)` directly on the root client still sends data when you call those methods explicitly.

## Flushing

The SDK batches tracking-related events.

You can flush manually:

```csharp
await client.FlushAsync();
```

By default, the SDK also attempts to flush on shutdown:

- `AddReflag(...)` flushes during hosted-service shutdown
- `DisposeAsync()` tries to flush before tearing down the client

This behavior is controlled by `Batch.FlushOnExit`:

```csharp
var client = new ReflagClient(new ReflagClientOptions
{
    SecretKey = Environment.GetEnvironmentVariable("REFLAG_SECRET_KEY"),
    Batch = new ReflagBatchOptions
    {
        FlushOnExit = false,
    },
});
```

## Tracking custom events and setting custom attributes

The SDK supports:

- `UpdateUserAsync(...)`
- `UpdateCompanyAsync(...)`
- `TrackAsync(...)`
- `ReflagBoundClient.TrackAsync(...)`

Example with the root client:

```csharp
await client.UpdateUserAsync("user-123", new ReflagTrackOptions
{
    Attributes = new Dictionary<string, object?>
    {
        ["name"] = "Ada",
        ["plan"] = "pro",
    },
    Active = true,
});

await client.UpdateCompanyAsync("company-456", new ReflagCompanyTrackOptions
{
    UserId = "user-123",
    Attributes = new Dictionary<string, object?>
    {
        ["name"] = "Acme",
        ["tier"] = "enterprise",
    },
    Active = true,
});

await client.TrackAsync("user-123", "checkout-started", new ReflagEventTrackOptions
{
    CompanyId = "company-456",
    Attributes = new Dictionary<string, object?>
    {
        ["cartValue"] = 199,
    },
    Active = true,
});
```

And with a bound client:

```csharp
var bound = client.BindClient(new ReflagContext
{
    User = new ReflagUserContext
    {
        Id = "user-123",
        Name = "Ada",
        Email = "ada@example.com",
    },
    Company = new ReflagCompanyContext
    {
        Id = "company-456",
        Name = "Acme",
    },
});

await bound.TrackAsync("checkout-started", new ReflagEventTrackOptions
{
    Attributes = new Dictionary<string, object?>
    {
        ["cartValue"] = 199,
    },
    Active = true,
});
```

Useful conventional attributes include:

- `name`
- `email`
- `avatar`

Attributes are modeled as `IReadOnlyDictionary<string, object?>` and serialized as JSON, so nested attribute values are supported.

## Managing `Last seen`

Use `Active` to control whether an update or telemetry event should count as activity in Reflag and update `Last seen`.

Examples:

```csharp
await client.UpdateUserAsync("user-123", new ReflagTrackOptions
{
    Attributes = new Dictionary<string, object?>
    {
        ["name"] = "Ada",
    },
    Active = true,
});

await client.UpdateCompanyAsync("company-456", new ReflagCompanyTrackOptions
{
    Attributes = new Dictionary<string, object?>
    {
        ["name"] = "Acme",
    },
    Active = false,
});
```

For automatic context sync triggered by `BindClient(...)`, `GetFlag(...)`, and `GetFlagsForBootstrap(...)`, use `ReflagTelemetryOptions.Active`:

```csharp
var bound = client.BindClient(
    new ReflagContext
    {
        User = new ReflagUserContext { Id = "user-123" },
        Company = new ReflagCompanyContext { Id = "company-456" },
    },
    new ReflagTelemetryOptions
    {
        Active = false,
    });
```

## License

MIT. See [LICENSE](LICENSE).
