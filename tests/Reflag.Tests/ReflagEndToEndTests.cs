using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Reflag.Internal;
using Xunit;

namespace Reflag.Tests;

public sealed class ReflagEndToEndTests
{
    [Fact]
    public async Task InitializeAsync_over_real_http_server_uses_wire_targeting_version_and_expected_local_evaluation()
    {
        await using var server = await LoopbackJsonServer.StartAsync(request =>
        {
            Assert.Equal("GET", request.Method);
            Assert.Equal("/api/features", request.Path);
            Assert.Equal("Bearer validSecretKeyWithMoreThan22Chars", request.Headers["Authorization"]);
            Assert.True(request.Headers.ContainsKey("reflag-sdk-version"));

            return LoopbackJsonResponse.Json(new
            {
                success = true,
                flagStateVersion = 123,
                features = new object[]
                {
                    new
                    {
                        key = "new-dashboard",
                        description = "Roll out the new dashboard",
                        targeting = new
                        {
                            version = 7,
                            rules = new object[]
                            {
                                new
                                {
                                    filter = new
                                    {
                                        type = "context",
                                        field = "company.id",
                                        @operator = "IS",
                                        values = new[] { "company-456" },
                                    },
                                    value = true,
                                },
                                new
                                {
                                    filter = new
                                    {
                                        type = "constant",
                                        value = true,
                                    },
                                    value = false,
                                },
                            },
                        },
                    },
                },
            });
        }, basePath: "/api/");

        await using var client = new ReflagClient(new ReflagClientOptions
        {
            SecretKey = "validSecretKeyWithMoreThan22Chars",
            ApiBaseUrl = server.BaseUri,
            FlagsFetchRetries = 0,
            Batch = new ReflagBatchOptions
            {
                FlushOnExit = false,
            },
        });

        await client.InitializeAsync();

        var matchingContext = new ReflagContext
        {
            Company = new ReflagCompanyContext
            {
                Id = "company-456",
            },
        };

        var nonMatchingContext = new ReflagContext
        {
            Company = new ReflagCompanyContext
            {
                Id = "company-999",
            },
        };

        var enabled = client.GetFlag("new-dashboard", matchingContext);
        var disabled = client.GetFlag("new-dashboard", nonMatchingContext);
        var bootstrapped = client.GetFlagsForBootstrap(matchingContext);
        var definitions = client.GetFlagDefinitions();

        Assert.True(enabled);
        Assert.False(disabled);

        Assert.Equal(123, bootstrapped.FlagStateVersion);

        var bootstrappedFlag = bootstrapped.Flags["new-dashboard"];
        Assert.True(bootstrappedFlag.Value);
        Assert.Equal(7, bootstrappedFlag.TargetingVersion);
        Assert.Equal(new[] { true, true }, bootstrappedFlag.RuleEvaluationResults);
        Assert.Empty(bootstrappedFlag.MissingContextFields ?? Array.Empty<string>());

        Assert.Single(definitions);
        Assert.Equal("new-dashboard", definitions[0].Key);
        Assert.Equal(7, definitions[0].Targeting.Version);

        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task InitializeAsync_over_real_http_server_supports_backend_boolean_rule_shape_without_rule_value()
    {
        await using var server = await LoopbackJsonServer.StartAsync(_ =>
            LoopbackJsonResponse.Json(new
            {
                success = true,
                flagStateVersion = 2,
                features = new object[]
                {
                    new
                    {
                        key = "f-1",
                        description = (string?)null,
                        stage = "In development",
                        targeting = new
                        {
                            version = 2,
                            rules = new object[]
                            {
                                new
                                {
                                    filter = new
                                    {
                                        type = "group",
                                        @operator = "and",
                                        filters = new object[]
                                        {
                                            new
                                            {
                                                type = "context",
                                                field = "company.id",
                                                @operator = "ANY_OF",
                                                values = new[] { "company-456" },
                                            },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            }), basePath: "/api/");

        await using var client = new ReflagClient(new ReflagClientOptions
        {
            SecretKey = "validSecretKeyWithMoreThan22Chars",
            ApiBaseUrl = server.BaseUri,
            FlagsFetchRetries = 0,
            Batch = new ReflagBatchOptions
            {
                FlushOnExit = false,
            },
        });

        await client.InitializeAsync();

        var bootstrapped = client.GetFlagsForBootstrap(new ReflagContext
        {
            Company = new ReflagCompanyContext
            {
                Id = "company-456",
            },
        });

        Assert.True(client.GetFlag("f-1", new ReflagContext
        {
            Company = new ReflagCompanyContext
            {
                Id = "company-456",
            },
        }));
        Assert.True(bootstrapped.Flags["f-1"].Value);
        Assert.Equal(2, bootstrapped.Flags["f-1"].TargetingVersion);
        Assert.Equal(new[] { true }, bootstrapped.Flags["f-1"].RuleEvaluationResults);
    }

    [Fact]
    public async Task GetFlag_and_FlushAsync_send_feature_flag_check_event_over_real_http_server()
    {
        await using var server = await LoopbackJsonServer.StartAsync(request =>
        {
            if (request.Method == "GET" && request.Path == "/api/features")
            {
                return LoopbackJsonResponse.Json(new
                {
                    success = true,
                    flagStateVersion = 1,
                    features = new object[]
                    {
                        new
                        {
                            key = "new-dashboard",
                            targeting = new
                            {
                                version = 7,
                                rules = new object[]
                                {
                                    new
                                    {
                                        filter = new
                                        {
                                            type = "context",
                                            field = "company.id",
                                            @operator = "IS",
                                            values = new[] { "company-456" },
                                        },
                                        value = true,
                                    },
                                },
                            },
                        },
                    },
                });
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
        var enabled = client.GetFlag(
            "new-dashboard",
            new ReflagContext
            {
                User = new ReflagUserContext
                {
                    Id = "user-123",
                },
                Company = new ReflagCompanyContext
                {
                    Id = "company-456",
                },
                Other = new Dictionary<string, object?>
                {
                    ["environment"] = "staging",
                },
            });

        await client.FlushAsync();

        Assert.True(enabled);
        Assert.Equal(2, server.Requests.Count);
        var bulkRequest = server.Requests.Single(request => request.Method == "POST");
        Assert.Equal("/api/bulk", bulkRequest.Path);

        using var body = JsonDocument.Parse(bulkRequest.Body);
        var items = body.RootElement.EnumerateArray().ToList();
        Assert.Equal(3, items.Count);

        var eventItem = items.Single(item => item.GetProperty("type").GetString() == "feature-flag-event");
        Assert.Equal("check", eventItem.GetProperty("action").GetString());
        Assert.Equal("new-dashboard", eventItem.GetProperty("key").GetString());
        Assert.Equal(7, eventItem.GetProperty("targetingVersion").GetInt32());
        Assert.True(eventItem.GetProperty("evalResult").GetBoolean());
        Assert.Equal("user-123", eventItem.GetProperty("evalContext").GetProperty("user").GetProperty("id").GetString());
        Assert.Equal("company-456", eventItem.GetProperty("evalContext").GetProperty("company").GetProperty("id").GetString());
        Assert.Equal("staging", eventItem.GetProperty("evalContext").GetProperty("other").GetProperty("environment").GetString());
    }

    [Fact]
    public async Task BindClient_and_FlushAsync_send_company_and_user_bulk_items_over_real_http_server()
    {
        await using var server = await LoopbackJsonServer.StartAsync(request =>
        {
            if (request.Method == "GET" && request.Path == "/api/features")
            {
                return LoopbackJsonResponse.Json(new
                {
                    success = true,
                    flagStateVersion = 1,
                    features = Array.Empty<object>(),
                });
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
        public static LoopbackJsonResponse Json(object body, int statusCode = 200)
        {
            return new LoopbackJsonResponse(
                statusCode,
                "application/json",
                JsonSerializer.Serialize(body, ReflagJson.Options));
        }
    }
}
