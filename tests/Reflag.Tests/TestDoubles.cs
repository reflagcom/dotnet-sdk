using System.Collections.Concurrent;
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

internal sealed class TestTransport : IReflagHttpTransport
{
    private readonly ConcurrentQueue<Func<Type, object?>> _getResponses = new();
    private readonly ConcurrentQueue<Func<Type, object?>> _postResponses = new();

    public List<(Uri Url, IReadOnlyDictionary<string, string> Headers, TimeSpan Timeout)> GetCalls { get; } = new();
    public List<(Uri Url, IReadOnlyDictionary<string, string> Headers, object? Body)> PostCalls { get; } = new();

    public void EnqueueGetJson(string json)
    {
        _getResponses.Enqueue(type => JsonSerializer.Deserialize(json, type, ReflagJson.Options));
    }

    public void EnqueueGetFailure(Exception exception)
    {
        _getResponses.Enqueue(_ => throw exception);
    }

    public void EnqueuePostSuccess()
    {
        _postResponses.Enqueue(type => Activator.CreateInstance(type, nonPublic: true) ?? JsonSerializer.Deserialize("{\"success\":true}", type, ReflagJson.Options)!);
    }

    public void EnqueuePostJson(string json)
    {
        _postResponses.Enqueue(type => JsonSerializer.Deserialize(json, type, ReflagJson.Options)!);
    }

    public void EnqueuePostFailure(Exception exception)
    {
        _postResponses.Enqueue(_ => throw exception);
    }

    public Task<ReflagHttpResponse<TResponse>> GetAsync<TResponse>(
        Uri url,
        IReadOnlyDictionary<string, string> headers,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        GetCalls.Add((url, headers, timeout));
        if (!_getResponses.TryDequeue(out var factory))
        {
            throw new InvalidOperationException("No queued GET response available.");
        }

        var body = (TResponse?)factory(typeof(TResponse));
        return Task.FromResult(new ReflagHttpResponse<TResponse>
        {
            StatusCode = 200,
            IsSuccessStatusCode = true,
            Body = body,
        });
    }

    public Task<ReflagHttpResponse<TResponse>> PostAsync<TRequest, TResponse>(
        Uri url,
        IReadOnlyDictionary<string, string> headers,
        TRequest body,
        CancellationToken cancellationToken = default)
    {
        PostCalls.Add((url, headers, body));

        if (!_postResponses.TryDequeue(out var factory))
        {
            factory = type => JsonSerializer.Deserialize("{\"success\":true}", type, ReflagJson.Options)!;
        }

        var responseBody = (TResponse?)factory(typeof(TResponse));
        return Task.FromResult(new ReflagHttpResponse<TResponse>
        {
            StatusCode = 200,
            IsSuccessStatusCode = true,
            Body = responseBody,
        });
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
