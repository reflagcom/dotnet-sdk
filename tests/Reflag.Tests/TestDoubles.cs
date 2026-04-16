using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Reflag.Internal;

namespace Reflag.Tests;

internal sealed class TestLogger : ILogger
{
    public List<(LogLevel Level, string Message)> Entries { get; } = new();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull
    {
        return NullScope.Instance;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}

internal sealed class TestTransport : HttpMessageHandler
{
    private readonly ConcurrentQueue<Func<HttpResponseMessage>> _getResponses = new();
    private readonly ConcurrentQueue<Func<HttpResponseMessage>> _postResponses = new();

    public List<(Uri Url, IReadOnlyDictionary<string, string> Headers)> GetCalls { get; } = new();
    public List<(Uri Url, IReadOnlyDictionary<string, string> Headers, string Body)> PostCalls { get; } = new();

    public HttpClient CreateHttpClient()
    {
        return new HttpClient(this);
    }

    public void EnqueueGetJson(string json)
    {
        _getResponses.Enqueue(() => CreateJsonResponse(json));
    }

    public void EnqueueGetFailure(Exception exception)
    {
        _getResponses.Enqueue(() => throw exception);
    }

    public void EnqueuePostSuccess()
    {
        _postResponses.Enqueue(() => CreateJsonResponse("{\"success\":true}"));
    }

    public void EnqueuePostJson(string json)
    {
        _postResponses.Enqueue(() => CreateJsonResponse(json));
    }

    public void EnqueuePostFailure(Exception exception)
    {
        _postResponses.Enqueue(() => throw exception);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is null)
        {
            throw new InvalidOperationException("Request URI was not provided.");
        }

        var headers = CaptureHeaders(request);
        if (request.Method == HttpMethod.Get)
        {
            GetCalls.Add((request.RequestUri, headers));
            if (!_getResponses.TryDequeue(out var factory))
            {
                throw new InvalidOperationException("No queued GET response available.");
            }

            return factory();
        }

        if (request.Method == HttpMethod.Post)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            PostCalls.Add((request.RequestUri, headers, body));

            if (!_postResponses.TryDequeue(out var factory))
            {
                factory = () => CreateJsonResponse("{\"success\":true}");
            }

            return factory();
        }

        throw new NotSupportedException($"Unexpected HTTP method '{request.Method}'.");
    }

    private static HttpResponseMessage CreateJsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private static IReadOnlyDictionary<string, string> CaptureHeaders(HttpRequestMessage request)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            headers[header.Key] = string.Join(",", header.Value);
        }

        return headers;
    }
}

internal static class TestDefinitions
{
    public static FlagDefinition CreateFlag(
        string key,
        int version,
        FlagFilterDefinition filter,
        bool value = true,
        string? description = null)
    {
        return new FlagDefinition
        {
            Key = key,
            Description = description,
            Targeting = new FlagTargetingDefinition
            {
                Version = version,
                Rules =
                [
                    new FlagTargetingRuleDefinition
                    {
                        Filter = filter,
                        Value = value,
                    },
                ],
            },
        };
    }
}
