using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Reflag.Internal;
using Xunit;

namespace Reflag.Tests;

public sealed class ReflagEndToEndTests
{
    // Shaped like GET /flags from ingest: Boolean and multivariate flags, a segment, a flag reference and
    // a percentage distribution keyed on the company.
    private const string FlagsWireResponse = """
        {
          "success": true,
          "protocolVersion": 2,
          "flagStateVersion": 123,
          "flags": [
            {
              "key": "new-dashboard",
              "sourceVersionId": "fv-dashboard",
              "variants": { "true": true, "false": false },
              "rules": [
                { "id": "company-targets", "filter": { "type": "context", "field": "company.id", "operator": "ANY_OF", "values": ["company-456"] }, "result": { "type": "variant", "variantKey": "true" } },
                { "id": "default", "filter": { "type": "constant", "value": true }, "result": { "type": "variant", "variantKey": "false" } }
              ]
            },
            {
              "key": "model",
              "sourceVersionId": "fv-model",
              "variants": {
                "quality": { "name": "large", "maxTokens": 8000 },
                "cheap": { "name": "small", "maxTokens": 1000 }
              },
              "rules": [
                { "id": "enterprise", "filter": { "type": "segment", "key": "enterprise" }, "result": { "type": "variant", "variantKey": "quality" } },
                { "id": "split", "filter": { "type": "constant", "value": true }, "result": { "type": "percentageDistribution", "key": "[\"model\",\"split\"]", "attribute": "company.id", "allocations": [ { "percentage": 0, "destination": { "type": "variant", "variantKey": "quality" } }, { "percentage": 100, "destination": { "type": "variant", "variantKey": "cheap" } } ] } },
                { "id": "default", "filter": { "type": "constant", "value": true }, "result": { "type": "variant", "variantKey": "cheap" } }
              ]
            },
            {
              "key": "quality-banner",
              "sourceVersionId": "fv-banner",
              "variants": { "true": true, "false": false },
              "rules": [
                { "id": "uses-quality", "filter": { "type": "flag", "key": "model", "variantKey": "quality" }, "result": { "type": "variant", "variantKey": "true" } },
                { "id": "default", "filter": { "type": "constant", "value": true }, "result": { "type": "variant", "variantKey": "false" } }
              ]
            }
          ],
          "segments": [
            { "key": "enterprise", "filter": { "type": "context", "field": "company.plan", "operator": "IS", "values": ["enterprise"] } }
          ]
        }
        """;

    private sealed record ModelConfig(string Name, int MaxTokens);

    [Fact]
    public async Task InitializeAsync_over_real_http_server_evaluates_protocol_v2_collection_locally()
    {
        await using var server = await LoopbackJsonServer.StartAsync(request =>
        {
            Assert.Equal("GET", request.Method);
            Assert.Equal("/api/flags", request.Path);
            Assert.Equal("Bearer validSecretKeyWithMoreThan22Chars", request.Headers["Authorization"]);
            Assert.True(request.Headers.ContainsKey("reflag-sdk-version"));
            return LoopbackJsonResponse.Raw(FlagsWireResponse);
        }, basePath: "/api/");

        await using var client = new ReflagClient(new ReflagClientOptions
        {
            SecretKey = "validSecretKeyWithMoreThan22Chars",
            ApiBaseUrl = server.BaseUri,
            FlagsFetchRetries = 0,
            Batch = new ReflagBatchOptions { FlushOnExit = false },
        });

        await client.InitializeAsync();

        var enterprise = ReflagContext.From(new { Company = new { Id = "company-456", plan = "enterprise" } });
        var free = ReflagContext.From(new { Company = new { Id = "company-999", plan = "free" } });
        var fallbackModel = new ModelConfig("fallback", 1);

        Assert.True(client.GetFlag("new-dashboard", enterprise));
        Assert.False(client.GetFlag("new-dashboard", free));
        Assert.Equal(new ModelConfig("large", 8000), client.GetFlag("model", enterprise, fallbackModel));
        Assert.Equal(new ModelConfig("small", 1000), client.GetFlag("model", free, fallbackModel));
        Assert.True(client.GetFlag("quality-banner", enterprise));
        Assert.False(client.GetFlag("quality-banner", free));

        // Category mismatches and missing flags use the default.
        Assert.Equal("fallback", client.GetFlag("model", enterprise, "fallback"));
        Assert.False(client.GetFlag("model", enterprise));
        Assert.Equal(fallbackModel, client.GetFlag("missing", enterprise, fallbackModel));

        var values = client.GetFlags(free);
        Assert.Equal(new[] { "new-dashboard", "model", "quality-banner" }, values.Keys);
        Assert.Equal("small", values["model"]!.Value.GetProperty("name").GetString());

        var definitions = client.GetFlagDefinitions();
        Assert.Equal(new[] { "new-dashboard", "model", "quality-banner" }, definitions.Flags.Select(flag => flag.Key));
        Assert.Equal("enterprise", Assert.Single(definitions.Segments).Key);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task GetFlagsForBootstrap_serializes_protocol_v2_payload()
    {
        await using var server = await LoopbackJsonServer.StartAsync(_ => LoopbackJsonResponse.Raw(FlagsWireResponse), basePath: "/api/");
        await using var client = new ReflagClient(new ReflagClientOptions
        {
            SecretKey = "validSecretKeyWithMoreThan22Chars",
            ApiBaseUrl = server.BaseUri,
            FlagsFetchRetries = 0,
            Batch = new ReflagBatchOptions { FlushOnExit = false },
        });
        await client.InitializeAsync();

        var bootstrapped = client.GetFlagsForBootstrap(
            ReflagContext.From(new { User = new { Id = "user-1" }, Company = new { plan = "free", Id = "company-999" } }));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(bootstrapped));
        var root = json.RootElement;

        Assert.Equal(2, root.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(123, root.GetProperty("flagStateVersion").GetInt32());
        // Context keys are canonical (sorted) and use wire names.
        Assert.Equal(
            "{\"company\":{\"id\":\"company-999\",\"plan\":\"free\"},\"user\":{\"id\":\"user-1\"}}",
            root.GetProperty("context").GetRawText());

        var model = root.GetProperty("flags").GetProperty("model");
        Assert.Equal("model", model.GetProperty("flagKey").GetString());
        Assert.True(model.GetProperty("resolved").GetBoolean());
        Assert.Equal("cheap", model.GetProperty("variantKey").GetString());
        Assert.Equal("fv-model", model.GetProperty("sourceVersionId").GetString());
        Assert.Equal("split", model.GetProperty("matchedRuleId").GetString());
        Assert.Equal(1, model.GetProperty("allocationIndex").GetInt32());
        Assert.Equal("small", model.GetProperty("value").GetProperty("name").GetString());
        Assert.Equal(
            "[{\"ruleId\":\"enterprise\",\"matched\":false},{\"ruleId\":\"split\",\"matched\":true,\"allocationIndex\":1,\"destination\":{\"type\":\"variant\",\"variantKey\":\"cheap\"}}]",
            model.GetProperty("ruleResults").GetRawText());
        Assert.Equal(0, model.GetProperty("errors").GetArrayLength());

        // Bootstrapping is not a read: only user/company updates are sent, never checks.
        await client.FlushAsync();
        Assert.DoesNotContain(server.Requests, request => request.Body.Contains("feature-flag-event", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetFlag_and_FlushAsync_send_variant_check_event_over_real_http_server()
    {
        await using var server = await LoopbackJsonServer.StartAsync(request =>
        {
            if (request.Method == "GET" && request.Path == "/api/flags")
            {
                return LoopbackJsonResponse.Raw(FlagsWireResponse);
            }

            if (request.Method == "POST" && request.Path == "/api/bulk")
            {
                return LoopbackJsonResponse.Json(new { success = true });
            }

            return LoopbackJsonResponse.Json(new { success = false }, statusCode: 404);
        }, basePath: "/api/");

        await using var client = new ReflagClient(new ReflagClientOptions
        {
            SecretKey = "validSecretKeyWithMoreThan22Chars",
            ApiBaseUrl = server.BaseUri,
            FlagsFetchRetries = 0,
        });

        await client.InitializeAsync();
        var model = client.GetFlag(
            "model",
            new ReflagContext
            {
                User = new ReflagUserContext { Id = "user-123" },
                Company = new ReflagCompanyContext
                {
                    Id = "company-456",
                    Attributes = new Dictionary<string, object?> { ["plan"] = "enterprise" },
                },
                Other = new Dictionary<string, object?> { ["environment"] = "staging" },
            },
            new ModelConfig("fallback", 1));

        await client.FlushAsync();

        Assert.Equal("large", model.Name);
        Assert.Equal(2, server.Requests.Count);
        var bulkRequest = server.Requests.Single(request => request.Method == "POST");
        using var body = JsonDocument.Parse(bulkRequest.Body);
        var items = body.RootElement.EnumerateArray().ToList();
        Assert.Equal(3, items.Count);

        var eventItem = items.Single(item => item.GetProperty("type").GetString() == "feature-flag-event");
        Assert.Equal("check", eventItem.GetProperty("action").GetString());
        Assert.Equal("model", eventItem.GetProperty("key").GetString());
        // The selected variant key is reported, never the JSON payload.
        Assert.Equal("quality", eventItem.GetProperty("evalResult").GetString());
        Assert.Equal("fv-model", eventItem.GetProperty("sourceVersionId").GetString());
        Assert.Equal("enterprise", eventItem.GetProperty("matchedRuleId").GetString());
        Assert.Equal(123, eventItem.GetProperty("flagStateVersion").GetInt32());
        Assert.Equal("[true]", eventItem.GetProperty("evalRuleResults").GetRawText());
        Assert.False(eventItem.TryGetProperty("targetingVersion", out _));
        Assert.False(eventItem.TryGetProperty("evalErrors", out _));
        Assert.Equal("user-123", eventItem.GetProperty("evalContext").GetProperty("user").GetProperty("id").GetString());
        Assert.Equal("enterprise", eventItem.GetProperty("evalContext").GetProperty("company").GetProperty("plan").GetString());
        Assert.Equal("staging", eventItem.GetProperty("evalContext").GetProperty("other").GetProperty("environment").GetString());
    }

    [Fact]
    public async Task InitializeAsync_rejects_invalid_collections_without_retrying()
    {
        await using var server = await LoopbackJsonServer.StartAsync(_ => LoopbackJsonResponse.Raw("""
            {
              "success": true,
              "protocolVersion": 2,
              "flagStateVersion": 1,
              "flags": [
                { "key": "a", "sourceVersionId": "v", "variants": { "on": true }, "rules": [ { "id": "r", "filter": { "type": "segment", "key": "ghost" }, "result": { "type": "variant", "variantKey": "on" } }, { "id": "default", "filter": { "type": "constant", "value": true }, "result": { "type": "variant", "variantKey": "on" } } ] }
              ],
              "segments": []
            }
            """), basePath: "/api/");
        var logger = new TestLogger();
        await using var client = new ReflagClient(new ReflagClientOptions
        {
            SecretKey = "validSecretKeyWithMoreThan22Chars",
            ApiBaseUrl = server.BaseUri,
            FlagsFetchRetries = 3,
            Logger = logger,
            Batch = new ReflagBatchOptions { FlushOnExit = false },
        });

        await client.InitializeAsync();

        Assert.Single(server.Requests);
        Assert.Empty(client.GetFlagDefinitions().Flags);
        Assert.False(client.GetFlag("a", new ReflagContext(), new ReflagTelemetryOptions { EnableTelemetry = false }));
    }

    [Fact]
    public async Task BindClient_and_FlushAsync_send_company_and_user_bulk_items_over_real_http_server()
    {
        await using var server = await LoopbackJsonServer.StartAsync(request =>
        {
            if (request.Method == "GET" && request.Path == "/api/flags")
            {
                return LoopbackJsonResponse.Raw(TestDefinitions.EmptyFlagsResponse);
            }

            if (request.Method == "POST" && request.Path == "/api/bulk")
            {
                return LoopbackJsonResponse.Json(new { success = true });
            }

            return LoopbackJsonResponse.Json(new { success = false }, statusCode: 404);
        }, basePath: "/api/");

        await using var client = new ReflagClient(new ReflagClientOptions
        {
            SecretKey = "validSecretKeyWithMoreThan22Chars",
            ApiBaseUrl = server.BaseUri,
            FlagsFetchRetries = 0,
        });

        await client.InitializeAsync();
        _ = client.BindClient(new ReflagContext
        {
            User = new ReflagUserContext
            {
                Id = "user-123",
                Name = "Ada",
            },
            Company = new ReflagCompanyContext
            {
                Id = "company-456",
                Name = "Acme",
            },
        }, new ReflagTelemetryOptions
        {
            Active = true,
        });

        await client.FlushAsync();

        Assert.Equal(2, server.Requests.Count);
        var bulkRequest = server.Requests.Single(request => request.Method == "POST");
        Assert.Equal("/api/bulk", bulkRequest.Path);
        Assert.Equal("Bearer validSecretKeyWithMoreThan22Chars", bulkRequest.Headers["Authorization"]);

        using var body = JsonDocument.Parse(bulkRequest.Body);
        var items = body.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, items.Count);

        var companyItem = items.Single(item => item.GetProperty("type").GetString() == "company");
        Assert.Equal("company-456", companyItem.GetProperty("companyId").GetString());
        Assert.Equal("user-123", companyItem.GetProperty("userId").GetString());
        Assert.Equal("Acme", companyItem.GetProperty("attributes").GetProperty("name").GetString());
        Assert.True(companyItem.GetProperty("context").GetProperty("active").GetBoolean());

        var userItem = items.Single(item => item.GetProperty("type").GetString() == "user");
        Assert.Equal("user-123", userItem.GetProperty("userId").GetString());
        Assert.Equal("Ada", userItem.GetProperty("attributes").GetProperty("name").GetString());
        Assert.True(userItem.GetProperty("context").GetProperty("active").GetBoolean());
    }

    private sealed class LoopbackJsonServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly Func<LoopbackJsonRequest, LoopbackJsonResponse> _handler;
        private readonly Task _serveTask;

        public Uri BaseUri { get; }

        public List<LoopbackJsonRequest> Requests { get; } = new();

        private LoopbackJsonServer(Uri baseUri, Func<LoopbackJsonRequest, LoopbackJsonResponse> handler)
        {
            BaseUri = baseUri;
            _handler = handler;
            _listener = new HttpListener();
            _listener.Prefixes.Add(baseUri.AbsoluteUri);
            _listener.Start();
            _serveTask = Task.Run(ServeAsync);
        }

        public static Task<LoopbackJsonServer> StartAsync(
            Func<LoopbackJsonRequest, LoopbackJsonResponse> handler,
            string basePath = "/")
        {
            var port = GetFreePort();
            var normalizedBasePath = basePath.StartsWith('/') ? basePath : $"/{basePath}";
            if (!normalizedBasePath.EndsWith('/'))
            {
                normalizedBasePath += "/";
            }

            var baseUri = new Uri($"http://127.0.0.1:{port}{normalizedBasePath}");
            return Task.FromResult(new LoopbackJsonServer(baseUri, handler));
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            _listener.Close();

            try
            {
                await _serveTask.ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                // expected during shutdown
            }
            catch (ObjectDisposedException)
            {
                // expected during shutdown
            }
        }

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                var request = await CaptureRequestAsync(context.Request).ConfigureAwait(false);
                Requests.Add(request);

                var response = _handler(request);
                var responseBytes = Encoding.UTF8.GetBytes(response.Body);

                context.Response.StatusCode = response.StatusCode;
                context.Response.ContentType = response.ContentType;
                context.Response.ContentEncoding = Encoding.UTF8;
                context.Response.ContentLength64 = responseBytes.Length;
                await context.Response.OutputStream.WriteAsync(responseBytes).ConfigureAwait(false);
                context.Response.Close();
            }
        }

        private static async Task<LoopbackJsonRequest> CaptureRequestAsync(HttpListenerRequest request)
        {
            using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8, leaveOpen: true);
            var body = request.HasEntityBody
                ? await reader.ReadToEndAsync().ConfigureAwait(false)
                : string.Empty;

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in request.Headers.AllKeys)
            {
                if (key is null)
                {
                    continue;
                }

                headers[key] = request.Headers[key] ?? string.Empty;
            }

            return new LoopbackJsonRequest(
                request.HttpMethod,
                request.Url?.AbsolutePath ?? "/",
                headers,
                body);
        }

        private static int GetFreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }

    private sealed record LoopbackJsonRequest(
        string Method,
        string Path,
        IReadOnlyDictionary<string, string> Headers,
        string Body);

    private sealed record LoopbackJsonResponse(int StatusCode, string ContentType, string Body)
    {
        public static LoopbackJsonResponse Raw(string body, int statusCode = 200)
        {
            return new LoopbackJsonResponse(statusCode, "application/json", body);
        }

        public static LoopbackJsonResponse Json(object body, int statusCode = 200)
        {
            return new LoopbackJsonResponse(
                statusCode,
                "application/json",
                JsonSerializer.Serialize(body, ReflagJson.Options));
        }
    }
}
