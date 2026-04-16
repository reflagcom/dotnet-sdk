using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Reflag.Internal;
using Xunit;

namespace Reflag.Tests;

public sealed class PushSyncTests
{
    private const string SecretKey = "validSecretKeyWithMoreThan22Chars";

    [Fact]
    public async Task Push_mode_is_the_default_sync_mode()
    {
        var waitForVersionSeen = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await AsyncLoopbackServer.StartAsync(async (request, context, cancellationToken) =>
        {
            if (request.Path == "/features")
            {
                if (request.Query.TryGetValue("waitForVersion", out var waitForVersion) && waitForVersion == "2")
                {
                    waitForVersionSeen.TrySetResult(null);
                }

                await AsyncLoopbackServer.WriteJsonAsync(context, new
                {
                    success = true,
                    flagStateVersion = 1,
                    features = Array.Empty<object>(),
                }).ConfigureAwait(false);
                return;
            }

            if (request.Path == "/sse")
            {
                await AsyncLoopbackServer.WriteSseAsync(
                    context,
                    "event: message\n" +
                    "data: {\"name\":\"flags-updated\",\"data\":\"{\\\"flagStateVersion\\\":2}\"}\n\n",
                    keepOpen: false,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await AsyncLoopbackServer.WriteJsonAsync(context, new { success = false }, statusCode: 404).ConfigureAwait(false);
        });

        await using var client = new ReflagClient(new ReflagClientOptions
        {
            SecretKey = SecretKey,
            ApiBaseUrl = server.BaseUri,
            FlagsPushUrl = new Uri(server.BaseUri, "sse"),
            FlagsFetchRetries = 0,
        });

        await client.InitializeAsync();
        await WaitUntilAsync(() => waitForVersionSeen.Task.IsCompleted, TimeSpan.FromSeconds(5));

        Assert.Contains(server.Requests, request => request.Path == "/features" && request.Query.TryGetValue("waitForVersion", out var value) && value == "2");
    }

    [Fact]
    public async Task Push_mode_refreshes_with_waitForVersion_when_flags_updated_message_is_received()
    {
        var waitForVersionSeen = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await AsyncLoopbackServer.StartAsync(async (request, context, cancellationToken) =>
        {
            if (request.Path == "/features")
            {
                var version = request.Query.TryGetValue("waitForVersion", out var waitForVersion) && waitForVersion == "2"
                    ? 2
                    : 1;

                if (version == 2)
                {
                    waitForVersionSeen.TrySetResult(null);
                }

                await AsyncLoopbackServer.WriteJsonAsync(context, new
                {
                    success = true,
                    flagStateVersion = version,
                    features = new object[]
                    {
                        new
                        {
                            key = "f1",
                            targeting = new
                            {
                                version,
                                rules = new object[]
                                {
                                    new
                                    {
                                        filter = new
                                        {
                                            type = "constant",
                                            value = true,
                                        },
                                        value = version == 2,
                                    },
                                },
                            },
                        },
                    },
                }).ConfigureAwait(false);
                return;
            }

            if (request.Path == "/sse")
            {
                Assert.Equal("Bearer validSecretKeyWithMoreThan22Chars", request.Headers["Authorization"]);
                Assert.Equal("text/event-stream", request.Headers["Accept"]);
                Assert.Equal("no-cache", request.Headers["Cache-Control"]);
                Assert.Equal($"flags-state:{Hashing.HashString(SecretKey)[..16]}", request.Query["channels"]);

                await AsyncLoopbackServer.WriteSseAsync(
                    context,
                    "event: message\n" +
                    "data: {\"name\":\"flags-updated\",\"data\":\"{\\\"flagStateVersion\\\":2}\"}\n\n",
                    keepOpen: false,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await AsyncLoopbackServer.WriteJsonAsync(context, new { success = false }, statusCode: 404).ConfigureAwait(false);
        });

        await using var client = new ReflagClient(new ReflagClientOptions
        {
            SecretKey = SecretKey,
            ApiBaseUrl = server.BaseUri,
            FlagsPushUrl = new Uri(server.BaseUri, "sse"),
            FlagsSyncMode = ReflagFlagsSyncMode.Push,
            FlagsFetchRetries = 0,
        });

        await client.InitializeAsync();
        await WaitUntilAsync(() => waitForVersionSeen.Task.IsCompleted, TimeSpan.FromSeconds(5));
        await client.FlushAsync();

        var enabled = client.GetFlag("f1", new ReflagContext(), new ReflagTelemetryOptions
        {
            EnableTelemetry = false,
        });

        Assert.True(enabled);
        Assert.Contains(server.Requests, request => request.Path == "/features" && request.Query.TryGetValue("waitForVersion", out var value) && value == "2");
    }

    [Fact]
    public async Task Push_mode_triggers_full_refresh_after_sse_reconnect()
    {
        var featuresRequests = 0;
        var reconnectRefreshSeen = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sseConnections = 0;

        await using var server = await AsyncLoopbackServer.StartAsync(async (request, context, cancellationToken) =>
        {
            if (request.Path == "/features")
            {
                var count = Interlocked.Increment(ref featuresRequests);
                if (count >= 2 && !request.Query.ContainsKey("waitForVersion"))
                {
                    reconnectRefreshSeen.TrySetResult(null);
                }

                await AsyncLoopbackServer.WriteJsonAsync(context, new
                {
                    success = true,
                    flagStateVersion = 1,
                    features = Array.Empty<object>(),
                }).ConfigureAwait(false);
                return;
            }

            if (request.Path == "/sse")
            {
                var connectionNumber = Interlocked.Increment(ref sseConnections);
                if (connectionNumber == 1)
                {
                    await AsyncLoopbackServer.WriteSseAsync(context, string.Empty, keepOpen: false, cancellationToken).ConfigureAwait(false);
                    return;
                }

                await AsyncLoopbackServer.WriteSseAsync(context, ": connected\n\n", keepOpen: true, cancellationToken).ConfigureAwait(false);
                return;
            }

            await AsyncLoopbackServer.WriteJsonAsync(context, new { success = false }, statusCode: 404).ConfigureAwait(false);
        });

        await using var client = new ReflagClient(new ReflagClientOptions
        {
            SecretKey = SecretKey,
            ApiBaseUrl = server.BaseUri,
            FlagsPushUrl = new Uri(server.BaseUri, "sse"),
            FlagsSyncMode = ReflagFlagsSyncMode.Push,
            FlagsFetchRetries = 0,
        });

        await client.InitializeAsync();
        await WaitUntilAsync(() => reconnectRefreshSeen.Task.IsCompleted, TimeSpan.FromSeconds(15));
        await client.FlushAsync();

        Assert.True(Volatile.Read(ref sseConnections) >= 2);
        Assert.Equal(2, server.Requests.Count(request => request.Path == "/features"));
        Assert.All(
            server.Requests.Where(request => request.Path == "/features"),
            request => Assert.False(request.Query.ContainsKey("waitForVersion")));
    }

    [Fact]
    public async Task Push_mode_updates_observable_flag_value_after_sse_update_between_get_flag_calls()
    {
        var allowUpdate = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitForVersionSeen = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await AsyncLoopbackServer.StartAsync(async (request, context, cancellationToken) =>
        {
            if (request.Path == "/features")
            {
                var version = request.Query.TryGetValue("waitForVersion", out var waitForVersion) && waitForVersion == "2"
                    ? 2
                    : 1;

                if (version == 2)
                {
                    waitForVersionSeen.TrySetResult(null);
                }

                await AsyncLoopbackServer.WriteJsonAsync(context, new
                {
                    success = true,
                    flagStateVersion = version,
                    features = new object[]
                    {
                        new
                        {
                            key = "f1",
                            targeting = new
                            {
                                version,
                                rules = new object[]
                                {
                                    new
                                    {
                                        filter = new
                                        {
                                            type = "constant",
                                            value = true,
                                        },
                                        value = version == 2,
                                    },
                                },
                            },
                        },
                    },
                }).ConfigureAwait(false);
                return;
            }

            if (request.Path == "/sse")
            {
                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/event-stream";
                context.Response.ContentEncoding = Encoding.UTF8;
                context.Response.SendChunked = true;

                var commentBytes = Encoding.UTF8.GetBytes(": connected\n\n");
                await context.Response.OutputStream.WriteAsync(commentBytes, cancellationToken).ConfigureAwait(false);
                await context.Response.OutputStream.FlushAsync(cancellationToken).ConfigureAwait(false);

                await allowUpdate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

                var updateBytes = Encoding.UTF8.GetBytes(
                    "event: message\n" +
                    "data: {\"name\":\"flags-updated\",\"data\":{\"flagStateVersion\":2}}\n\n");
                await context.Response.OutputStream.WriteAsync(updateBytes, cancellationToken).ConfigureAwait(false);
                await context.Response.OutputStream.FlushAsync(cancellationToken).ConfigureAwait(false);

                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // expected on shutdown
                }

                context.Response.Close();
                return;
            }

            await AsyncLoopbackServer.WriteJsonAsync(context, new { success = false }, statusCode: 404).ConfigureAwait(false);
        });

        await using var client = new ReflagClient(new ReflagClientOptions
        {
            SecretKey = SecretKey,
            ApiBaseUrl = server.BaseUri,
            FlagsPushUrl = new Uri(server.BaseUri, "sse"),
            FlagsSyncMode = ReflagFlagsSyncMode.Push,
            FlagsFetchRetries = 0,
        });

        await client.InitializeAsync();

        var before = client.GetFlag("f1", new ReflagContext(), new ReflagTelemetryOptions
        {
            EnableTelemetry = false,
        });

        Assert.False(before);

        allowUpdate.TrySetResult(null);
        await WaitUntilAsync(() => waitForVersionSeen.Task.IsCompleted, TimeSpan.FromSeconds(5));
        await WaitUntilAsync(
            () => client.GetFlag("f1", new ReflagContext(), new ReflagTelemetryOptions { EnableTelemetry = false }),
            TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Push_mode_handles_json_object_payloads_and_ignores_non_message_events()
    {
        var allowUpdate = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitForVersionSeen = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new TestLogger();

        await using var server = await AsyncLoopbackServer.StartAsync(async (request, context, cancellationToken) =>
        {
            if (request.Path == "/features")
            {
                var version = request.Query.TryGetValue("waitForVersion", out var waitForVersion) && waitForVersion == "3"
                    ? 3
                    : 1;

                if (version == 3)
                {
                    waitForVersionSeen.TrySetResult(null);
                }

                await AsyncLoopbackServer.WriteJsonAsync(context, new
                {
                    success = true,
                    flagStateVersion = version,
                    features = new object[]
                    {
                        new
                        {
                            key = "f1",
                            targeting = new
                            {
                                version,
                                rules = new object[]
                                {
                                    new
                                    {
                                        filter = new
                                        {
                                            type = "constant",
                                            value = true,
                                        },
                                        value = version == 3,
                                    },
                                },
                            },
                        },
                    },
                }).ConfigureAwait(false);
                return;
            }

            if (request.Path == "/sse")
            {
                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/event-stream";
                context.Response.ContentEncoding = Encoding.UTF8;
                context.Response.SendChunked = true;

                var ignoredEventBytes = Encoding.UTF8.GetBytes(
                    "event: ignored\n" +
                    "data: {\"name\":\"flags-updated\",\"data\":{\"flagStateVersion\":99}}\n\n");
                await context.Response.OutputStream.WriteAsync(ignoredEventBytes, cancellationToken).ConfigureAwait(false);
                await context.Response.OutputStream.FlushAsync(cancellationToken).ConfigureAwait(false);

                await allowUpdate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

                var updateBytes = Encoding.UTF8.GetBytes(
                    "data: {\"name\":\"flags-updated\",\"data\":{\"flagStateVersion\":3}}\n\n");
                await context.Response.OutputStream.WriteAsync(updateBytes, cancellationToken).ConfigureAwait(false);
                await context.Response.OutputStream.FlushAsync(cancellationToken).ConfigureAwait(false);

                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // expected on shutdown
                }

                context.Response.Close();
                return;
            }

            await AsyncLoopbackServer.WriteJsonAsync(context, new { success = false }, statusCode: 404).ConfigureAwait(false);
        });

        await using var client = new ReflagClient(new ReflagClientOptions
        {
            SecretKey = SecretKey,
            ApiBaseUrl = server.BaseUri,
            FlagsPushUrl = new Uri(server.BaseUri, "sse"),
            FlagsSyncMode = ReflagFlagsSyncMode.Push,
            FlagsFetchRetries = 0,
            Logger = logger,
        });

        await client.InitializeAsync();
        Assert.False(client.GetFlag("f1", new ReflagContext(), new ReflagTelemetryOptions { EnableTelemetry = false }));

        allowUpdate.TrySetResult(null);
        await WaitUntilAsync(() => waitForVersionSeen.Task.IsCompleted, TimeSpan.FromSeconds(5));
        Assert.True(client.GetFlag("f1", new ReflagContext(), new ReflagTelemetryOptions { EnableTelemetry = false }));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("failed to parse SSE message"));
        Assert.DoesNotContain(server.Requests, request => request.Path == "/features" && request.Query.TryGetValue("waitForVersion", out var value) && value == "99");
    }

    [Fact]
    public async Task Push_mode_initialize_completes_when_sse_endpoint_returns_invalid_response()
    {
        var logger = new TestLogger();

        await using var server = await AsyncLoopbackServer.StartAsync(async (request, context, cancellationToken) =>
        {
            if (request.Path == "/features")
            {
                await AsyncLoopbackServer.WriteJsonAsync(context, new
                {
                    success = true,
                    flagStateVersion = 1,
                    features = new object[]
                    {
                        new
                        {
                            key = "f1",
                            targeting = new
                            {
                                version = 1,
                                rules = new object[]
                                {
                                    new
                                    {
                                        filter = new
                                        {
                                            type = "constant",
                                            value = true,
                                        },
                                        value = true,
                                    },
                                },
                            },
                        },
                    },
                }).ConfigureAwait(false);
                return;
            }

            if (request.Path == "/sse")
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "text/plain";
                var body = Encoding.UTF8.GetBytes("boom");
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
                context.Response.Close();
                return;
            }

            await AsyncLoopbackServer.WriteJsonAsync(context, new { success = false }, statusCode: 404).ConfigureAwait(false);
        });

        await using var client = new ReflagClient(new ReflagClientOptions
        {
            SecretKey = SecretKey,
            ApiBaseUrl = server.BaseUri,
            FlagsPushUrl = new Uri(server.BaseUri, "sse"),
            FlagsSyncMode = ReflagFlagsSyncMode.Push,
            FlagsFetchRetries = 0,
            Logger = logger,
        });

        await client.InitializeAsync();

        Assert.True(client.GetFlag("f1", new ReflagContext(), new ReflagTelemetryOptions { EnableTelemetry = false }));
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("flag updates SSE endpoint returned an invalid response"));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        Assert.True(condition(), $"Condition was not met within {timeout}.");
    }

    private sealed class AsyncLoopbackServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly Func<LoopbackRequest, HttpListenerContext, CancellationToken, Task> _handler;
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly ConcurrentBag<Task> _requestTasks = new();
        private readonly Task _serveTask;

        public Uri BaseUri { get; }

        public ConcurrentQueue<LoopbackRequest> Requests { get; } = new();

        private AsyncLoopbackServer(Uri baseUri, Func<LoopbackRequest, HttpListenerContext, CancellationToken, Task> handler)
        {
            BaseUri = baseUri;
            _handler = handler;
            _listener = new HttpListener();
            _listener.Prefixes.Add(baseUri.AbsoluteUri);
            _listener.Start();
            _serveTask = Task.Run(ServeAsync);
        }

        public static Task<AsyncLoopbackServer> StartAsync(Func<LoopbackRequest, HttpListenerContext, CancellationToken, Task> handler)
        {
            var port = GetFreePort();
            var baseUri = new Uri($"http://127.0.0.1:{port}/");
            return Task.FromResult(new AsyncLoopbackServer(baseUri, handler));
        }

        public async ValueTask DisposeAsync()
        {
            _cancellationTokenSource.Cancel();
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

            await Task.WhenAll(_requestTasks.ToArray()).ConfigureAwait(false);
            _cancellationTokenSource.Dispose();
        }

        public static async Task WriteJsonAsync(HttpListenerContext context, object body, int statusCode = 200)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, ReflagJson.Options));
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json";
            context.Response.ContentEncoding = Encoding.UTF8;
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            context.Response.Close();
        }

        public static async Task WriteSseAsync(HttpListenerContext context, string payload, bool keepOpen, CancellationToken cancellationToken)
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/event-stream";
            context.Response.ContentEncoding = Encoding.UTF8;
            context.Response.SendChunked = true;

            if (!string.IsNullOrEmpty(payload))
            {
                var bytes = Encoding.UTF8.GetBytes(payload);
                await context.Response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await context.Response.OutputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (keepOpen)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // expected on shutdown
                }
            }

            context.Response.Close();
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
                Requests.Enqueue(request);

                var task = Task.Run(async () =>
                {
                    try
                    {
                        await _handler(request, context, _cancellationTokenSource.Token).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        if (context.Response.OutputStream.CanWrite)
                        {
                            try
                            {
                                context.Response.StatusCode = 500;
                                context.Response.Close();
                            }
                            catch
                            {
                                // ignore secondary failures
                            }
                        }

                        throw;
                    }
                }, _cancellationTokenSource.Token);

                _requestTasks.Add(task);
            }
        }

        private static async Task<LoopbackRequest> CaptureRequestAsync(HttpListenerRequest request)
        {
            using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8, leaveOpen: true);
            var body = request.HasEntityBody
                ? await reader.ReadToEndAsync().ConfigureAwait(false)
                : string.Empty;

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in request.Headers.AllKeys)
            {
                if (key is not null)
                {
                    headers[key] = request.Headers[key] ?? string.Empty;
                }
            }

            var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (request.Url is not null)
            {
                var rawQuery = request.Url.Query.StartsWith('?') ? request.Url.Query[1..] : request.Url.Query;
                foreach (var part in rawQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var equalsIndex = part.IndexOf('=');
                    var key = equalsIndex < 0 ? part : part[..equalsIndex];
                    var value = equalsIndex < 0 ? string.Empty : part[(equalsIndex + 1)..];
                    query[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value);
                }
            }

            return new LoopbackRequest(
                request.HttpMethod,
                request.Url?.AbsolutePath ?? "/",
                headers,
                query,
                body);
        }

        private static int GetFreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }

    private sealed record LoopbackRequest(
        string Method,
        string Path,
        IReadOnlyDictionary<string, string> Headers,
        IReadOnlyDictionary<string, string> Query,
        string Body);
}
