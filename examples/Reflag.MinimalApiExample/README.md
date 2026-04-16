# Reflag .NET SDK Minimal API example

## Run

```bash
dotnet run --project examples/Reflag.MinimalApiExample
```

To force the example app to consume the SDK's `netstandard2.0` asset instead of the `net10.0` asset, run the example as `net8.0`:

```bash
dotnet run --project examples/Reflag.MinimalApiExample -f net8.0
```

That works because the example app cannot use `Reflag`'s `net10.0` asset when it targets `net8.0`, so it falls back to the SDK's `netstandard2.0` build.

## Test

```bash
dotnet test examples/Reflag.MinimalApiExample.Tests/Reflag.MinimalApiExample.Tests.csproj
```

The example tests run the app in offline-demo mode and use `PushFlagOverrides(...)` to toggle flags on and off through the example endpoints.

The example starts in `offline-demo` mode when `REFLAG_SECRET_KEY` is not set.
In that mode:

- `demo-flag` is forced to `true`
- `new-dashboard` is forced to `false`

## Run against a live Reflag backend

```bash
export REFLAG_SECRET_KEY=your-secret-key

dotnet run --project examples/Reflag.MinimalApiExample
```

The live example uses the SDK's default push sync mode.

The example also uses the SDK's `AddReflag(...)` service-registration extension,
so initialization happens through the host lifecycle, Reflag logs flow through ASP.NET Core's normal `ILogger` pipeline,
and buffered telemetry is flushed on host shutdown by default.

To disable the shutdown flush behavior, set `Batch = new ReflagBatchOptions { FlushOnExit = false }` in your `ReflagClientOptions`.

## Optional: File fallback snapshots

The example can wire `ReflagFallbackProviders.File(...)` automatically when you set:

```bash
export REFLAG_FALLBACK_FILE_DIRECTORY="$PWD/.reflag-example"
```

When configured, the `/` endpoint reports:

```json
{
  "fallbackProvider": "file"
}
```

## Optional: Azure Blob fallback snapshots

The example can wire `ReflagFallbackProviders.AzureBlob(...)` automatically when you set:

```bash
export AZURE_STORAGE_CONNECTION_STRING='UseDevelopmentStorage=true'
export REFLAG_FALLBACK_AZURE_BLOB_CONTAINER='reflag-snapshots'
# optional
export REFLAG_FALLBACK_AZURE_BLOB_PREFIX='reflag/flags-fallback'
```

When configured, the `/` endpoint reports:

```json
{
  "fallbackProvider": "azure-blob"
}
```

## Try it

```bash
curl http://localhost:5000/
curl 'http://localhost:5000/flags/demo-flag'
curl 'http://localhost:5000/flags/new-dashboard?userId=user-123&companyId=company-456'
curl 'http://localhost:5000/flags/new-dashboard?context.user.id=user-123&context.company.id=company-456&context.company.plan=enterprise'
curl 'http://localhost:5000/bootstrap?userId=user-123&companyId=company-456'
curl 'http://localhost:5000/bootstrap?context.user.id=user-123&context.user.name=bleh&context.company.id=company-456'
curl 'http://localhost:5000/bootstrap?context.user.id=user-123&context.user.attributes.plan=enterprise&context.other.environment=staging'
```

The example now accepts `context.*` query params for both `/flags/{key}` and `/bootstrap`.
Useful patterns include:

- `context.user.id`, `context.user.name`, `context.user.email`, `context.user.avatar`
- `context.company.id`, `context.company.name`, `context.company.avatar`
- top-level custom fields like `context.company.plan=enterprise`
- explicit attribute paths like `context.user.attributes.plan=enterprise`
- explicit attribute paths like `context.company.attributes.tier=gold`
- `context.other.environment=staging`
- nested values like `context.other.device.os=ios`

The older `userId` and `companyId` shortcuts still work.

If ASP.NET chooses a different port, use the URL shown in the startup logs.
