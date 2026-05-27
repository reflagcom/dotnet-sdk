using System.Globalization;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reflag.Internal;

internal static class ReflagConstants
{
    public const string ApiBaseUrl = "https://front.reflag.com";
    public const string PubsubSseUrl = "https://front.reflag.com/sse/server";
    public const string SdkVersionHeaderName = "reflag-sdk-version";
    public const string SdkVersion = "dotnet-sdk/0.1.0"; // x-release-please-version
    public static readonly TimeSpan ApiTimeout = TimeSpan.FromMilliseconds(10_000);
    public static readonly TimeSpan FlagsRefetchInterval = TimeSpan.FromMilliseconds(60_000);
    public static readonly TimeSpan MinRefreshInterval = TimeSpan.FromMilliseconds(1_000);
    public static readonly TimeSpan FlagEventRateLimiterWindow = TimeSpan.FromMilliseconds(60_000);
    public static readonly TimeSpan SseInitialReconnectDelay = TimeSpan.FromMilliseconds(10_000);
    public static readonly TimeSpan SseMaxReconnectDelay = TimeSpan.FromMilliseconds(30_000);
    public const int BatchMaxSize = 100;
    public static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(10_000);
    public static readonly TimeSpan EndFlushTimeout = TimeSpan.FromMilliseconds(5_000);
}

internal static class ThrowHelpers
{
    public static void ThrowIfNull<T>(T? value, string paramName) where T : class
    {
        if (value is null)
        {
            throw new ArgumentNullException(paramName);
        }
    }

    public static void ThrowIfNullOrWhitespace(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{paramName} must be a non-empty string.", paramName);
        }
    }

    public static void ThrowIfNegative(int value, string paramName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(paramName, "Value must be greater than or equal to zero.");
        }
    }

    public static void ThrowIfNegative(TimeSpan value, string paramName)
    {
        if (value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(paramName, "Value must be greater than or equal to zero.");
        }
    }
}

internal static class Hashing
{
    public static string HashString(string value)
    {
        ThrowHelpers.ThrowIfNullOrWhitespace(value, nameof(value));
        return ToLowerHexString(ComputeSha256(Encoding.UTF8.GetBytes(value)));
    }

    public static int HashInt(string hashInput)
    {
        ThrowHelpers.ThrowIfNullOrWhitespace(hashInput, nameof(hashInput));
        var hash = ComputeSha256(Encoding.UTF8.GetBytes(hashInput));
        var value = ((uint)hash[0] | ((uint)hash[1] << 8) | ((uint)hash[2] << 16) | ((uint)hash[3] << 24)) & 0xFFFFF;
        return (int)Math.Floor((value / (double)0xFFFFF) * 100_000d);
    }

    private static byte[] ComputeSha256(byte[] value)
    {
#if NETSTANDARD2_0
        using var sha256 = SHA256.Create();
        return sha256.ComputeHash(value);
#else
        return SHA256.HashData(value);
#endif
    }

    private static string ToLowerHexString(byte[] value)
    {
        var builder = new StringBuilder(value.Length * 2);
        foreach (var b in value)
        {
            builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}

internal static class RandomHelpers
{
#if NETSTANDARD2_0
    private static readonly object Gate = new();
    private static readonly Random Random = new();
#endif

    public static double NextDouble()
    {
#if NETSTANDARD2_0
        lock (Gate)
        {
            return Random.NextDouble();
        }
#else
        return Random.Shared.NextDouble();
#endif
    }
}

internal static class ReflagJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        options.Converters.Add(new FlagFilterDefinitionJsonConverter());
        options.Converters.Add(new FlagContextFilterOperatorJsonConverter());
        options.Converters.Add(new FlagTargetingRuleDefinitionJsonConverter());
        options.Converters.Add(new FlagDefinitionJsonConverter());
        return options;
    }
}

internal sealed class FlagDefinitionJsonConverter : JsonConverter<FlagDefinition>
{
    public override FlagDefinition Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        var key = root.TryGetProperty("key", out var keyElement) && keyElement.ValueKind == JsonValueKind.String
            ? keyElement.GetString() ?? string.Empty
            : string.Empty;

        string? description = null;
        if (root.TryGetProperty("description", out var descriptionElement) &&
            descriptionElement.ValueKind != JsonValueKind.Null)
        {
            description = descriptionElement.GetString();
        }

        FlagTargetingDefinition? targeting = null;
        if (root.TryGetProperty("targeting", out var targetingElement))
        {
            targeting = JsonSerializer.Deserialize<FlagTargetingDefinition>(targetingElement.GetRawText(), options);
        }

        return new FlagDefinition
        {
            Key = key,
            Description = description,
            Targeting = targeting ?? new FlagTargetingDefinition(),
        };
    }

    public override void Write(Utf8JsonWriter writer, FlagDefinition value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("key", value.Key);

        if (value.Description is not null)
        {
            writer.WriteString("description", value.Description);
        }

        writer.WritePropertyName("targeting");
        JsonSerializer.Serialize(writer, value.Targeting, options);
        writer.WriteEndObject();
    }
}

internal sealed class FlagTargetingRuleDefinitionJsonConverter : JsonConverter<FlagTargetingRuleDefinition>
{
    public override FlagTargetingRuleDefinition Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        var filter = root.TryGetProperty("filter", out var filterElement)
            ? JsonSerializer.Deserialize<FlagFilterDefinition>(filterElement.GetRawText(), options)
            : null;

        var value = root.TryGetProperty("value", out var valueElement)
            ? valueElement.GetBoolean()
            : true;

        return new FlagTargetingRuleDefinition
        {
            Filter = filter ?? throw new JsonException("Flag rule must contain a filter."),
            Value = value,
        };
    }

    public override void Write(Utf8JsonWriter writer, FlagTargetingRuleDefinition value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("filter");
        JsonSerializer.Serialize(writer, value.Filter, options);
        writer.WriteBoolean("value", value.Value);
        writer.WriteEndObject();
    }
}

internal sealed class FlagFilterDefinitionJsonConverter : JsonConverter<FlagFilterDefinition>
{
    public override FlagFilterDefinition Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (!document.RootElement.TryGetProperty("type", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("Flag filter must contain a string 'type' property.");
        }

        var raw = document.RootElement.GetRawText();
        return typeElement.GetString() switch
        {
            "group" => JsonSerializer.Deserialize<FlagFilterGroupDefinition>(raw, options)
                       ?? throw new JsonException("Failed to deserialize group filter."),
            "negation" => JsonSerializer.Deserialize<FlagFilterNegationDefinition>(raw, options)
                           ?? throw new JsonException("Failed to deserialize negation filter."),
            "context" => JsonSerializer.Deserialize<FlagContextFilterDefinition>(raw, options)
                         ?? throw new JsonException("Failed to deserialize context filter."),
            "rolloutPercentage" => JsonSerializer.Deserialize<FlagPercentageRolloutFilterDefinition>(raw, options)
                                    ?? throw new JsonException("Failed to deserialize rollout filter."),
            "constant" => JsonSerializer.Deserialize<FlagConstantFilterDefinition>(raw, options)
                           ?? throw new JsonException("Failed to deserialize constant filter."),
            var unknown => throw new JsonException($"Unknown flag filter type '{unknown}'."),
        };
    }

    public override void Write(Utf8JsonWriter writer, FlagFilterDefinition value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case FlagFilterGroupDefinition group:
                JsonSerializer.Serialize(writer, group, options);
                break;
            case FlagFilterNegationDefinition negation:
                JsonSerializer.Serialize(writer, negation, options);
                break;
            case FlagContextFilterDefinition context:
                JsonSerializer.Serialize(writer, context, options);
                break;
            case FlagPercentageRolloutFilterDefinition rollout:
                JsonSerializer.Serialize(writer, rollout, options);
                break;
            case FlagConstantFilterDefinition constant:
                JsonSerializer.Serialize(writer, constant, options);
                break;
            default:
                throw new JsonException($"Unknown flag filter runtime type '{value.GetType().FullName}'.");
        }
    }
}

internal sealed class FlagContextFilterOperatorJsonConverter : JsonConverter<FlagContextFilterOperator>
{
    public override FlagContextFilterOperator Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Flag context filter operator must be a string.");
        }

        return reader.GetString() switch
        {
            "IS" => FlagContextFilterOperator.Is,
            "IS_NOT" => FlagContextFilterOperator.IsNot,
            "ANY_OF" => FlagContextFilterOperator.AnyOf,
            "NOT_ANY_OF" => FlagContextFilterOperator.NotAnyOf,
            "CONTAINS" => FlagContextFilterOperator.Contains,
            "NOT_CONTAINS" => FlagContextFilterOperator.NotContains,
            "GT" => FlagContextFilterOperator.Gt,
            "LT" => FlagContextFilterOperator.Lt,
            "AFTER" => FlagContextFilterOperator.After,
            "BEFORE" => FlagContextFilterOperator.Before,
            "DATE_AFTER" => FlagContextFilterOperator.DateAfter,
            "DATE_BEFORE" => FlagContextFilterOperator.DateBefore,
            "SET" => FlagContextFilterOperator.Set,
            "NOT_SET" => FlagContextFilterOperator.NotSet,
            "IS_TRUE" => FlagContextFilterOperator.IsTrue,
            "IS_FALSE" => FlagContextFilterOperator.IsFalse,
            var unknown => throw new JsonException($"Unknown flag context operator '{unknown}'."),
        };
    }

    public override void Write(Utf8JsonWriter writer, FlagContextFilterOperator value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            FlagContextFilterOperator.Is => "IS",
            FlagContextFilterOperator.IsNot => "IS_NOT",
            FlagContextFilterOperator.AnyOf => "ANY_OF",
            FlagContextFilterOperator.NotAnyOf => "NOT_ANY_OF",
            FlagContextFilterOperator.Contains => "CONTAINS",
            FlagContextFilterOperator.NotContains => "NOT_CONTAINS",
            FlagContextFilterOperator.Gt => "GT",
            FlagContextFilterOperator.Lt => "LT",
            FlagContextFilterOperator.After => "AFTER",
            FlagContextFilterOperator.Before => "BEFORE",
            FlagContextFilterOperator.DateAfter => "DATE_AFTER",
            FlagContextFilterOperator.DateBefore => "DATE_BEFORE",
            FlagContextFilterOperator.Set => "SET",
            FlagContextFilterOperator.NotSet => "NOT_SET",
            FlagContextFilterOperator.IsTrue => "IS_TRUE",
            FlagContextFilterOperator.IsFalse => "IS_FALSE",
            _ => throw new JsonException($"Unknown flag context operator '{value}'."),
        });
    }
}

internal static class ReflectionHelpers
{
    public static IEnumerable<KeyValuePair<string, object?>> EnumerateReadableProperties(object value)
    {
        return value.GetType()
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .OrderBy(property => property.MetadataToken)
            .Select(property => new KeyValuePair<string, object?>(property.Name, property.GetValue(value)));
    }

    public static bool IsScalarLike(object value)
    {
        var type = value.GetType();
        return type.IsPrimitive ||
               type.IsEnum ||
               value is string ||
               value is decimal ||
               value is DateTime ||
               value is DateTimeOffset ||
               value is Guid ||
               value is TimeSpan ||
               value is Uri;
    }

    public static string ConvertToFlatString(object value)
    {
        return value switch
        {
            bool boolValue => boolValue ? "true" : "false",
            DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
            DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        };
    }
}

internal sealed class ReflagSseTransportResponse(HttpResponseMessage? response, Stream? stream) : IDisposable
{
    public int StatusCode { get; init; }

    public bool IsSuccessStatusCode { get; init; }

    public string? ReasonPhrase { get; init; }

    public Stream? Stream { get; } = stream;

    public void Dispose()
    {
        Stream?.Dispose();
        response?.Dispose();
    }
}

internal sealed class TransportResponse<T>
{
    public int StatusCode { get; init; }

    public bool IsSuccessStatusCode { get; init; }

    public T? Body { get; init; }
}

internal sealed class HttpClientTransport : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public HttpClientTransport(HttpClient? httpClient = null, bool ownsHttpClient = false)
    {
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null || ownsHttpClient;
    }

    public async Task<TransportResponse<TResponse>> GetAsync<TResponse>(
        Uri url,
        IReadOnlyDictionary<string, string> headers,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddHeaders(request, headers);

        using var timeoutCts = timeout > TimeSpan.Zero
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;

        if (timeoutCts is not null)
        {
            timeoutCts.CancelAfter(timeout);
        }

        var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            timeoutCts?.Token ?? cancellationToken).ConfigureAwait(false);

        var body = await DeserializeResponseBodyAsync<TResponse>(response, cancellationToken).ConfigureAwait(false);
        return new TransportResponse<TResponse>
        {
            StatusCode = (int)response.StatusCode,
            IsSuccessStatusCode = response.IsSuccessStatusCode,
            Body = body,
        };
    }

    public async Task<TransportResponse<TResponse>> PostAsync<TRequest, TResponse>(
        Uri url,
        IReadOnlyDictionary<string, string> headers,
        TRequest body,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, ReflagJson.Options), Encoding.UTF8, "application/json"),
        };

        AddHeaders(request, headers);
        var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);

        var responseBody = await DeserializeResponseBodyAsync<TResponse>(response, cancellationToken).ConfigureAwait(false);
        return new TransportResponse<TResponse>
        {
            StatusCode = (int)response.StatusCode,
            IsSuccessStatusCode = response.IsSuccessStatusCode,
            Body = responseBody,
        };
    }

    public async Task<ReflagSseTransportResponse> OpenServerSentEventsAsync(
        Uri url,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddHeaders(request, headers);

        var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode || response.Content is null)
        {
            var invalidResponse = new ReflagSseTransportResponse(null, null)
            {
                StatusCode = (int)response.StatusCode,
                IsSuccessStatusCode = response.IsSuccessStatusCode,
                ReasonPhrase = response.ReasonPhrase,
            };

            response.Dispose();
            return invalidResponse;
        }

        var stream = await ReadAsStreamAsync(response.Content, cancellationToken).ConfigureAwait(false);
        return new ReflagSseTransportResponse(response, stream)
        {
            StatusCode = (int)response.StatusCode,
            IsSuccessStatusCode = response.IsSuccessStatusCode,
            ReasonPhrase = response.ReasonPhrase,
        };
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static async Task<TResponse?> DeserializeResponseBodyAsync<TResponse>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var stream = await ReadAsStreamAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (stream.CanSeek && stream.Length == 0)
        {
            throw new JsonException("Response body was empty.");
        }

        var deserialized = await JsonSerializer.DeserializeAsync<TResponse>(stream, ReflagJson.Options, cancellationToken)
            .ConfigureAwait(false);
        return deserialized;
    }

    private static Task<Stream> ReadAsStreamAsync(HttpContent content, CancellationToken cancellationToken)
    {
#if NETSTANDARD2_0
        return AsyncHelpers.WaitAsync(content.ReadAsStreamAsync(), cancellationToken);
#else
        return content.ReadAsStreamAsync(cancellationToken);
#endif
    }

    private static void AddHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string> headers)
    {
        foreach (var (name, value) in headers)
        {
            if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase) && request.Content is not null)
            {
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(value);
                continue;
            }

            request.Headers.TryAddWithoutValidation(name, value);
        }
    }
}

internal static class AsyncHelpers
{
    public static async Task WaitAsync(Task task, CancellationToken cancellationToken)
    {
        if (task.IsCompleted)
        {
            await task.ConfigureAwait(false);
            return;
        }

        var cancellationTask = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<object?>)state!).TrySetResult(null),
            cancellationTask);

        if (task != await Task.WhenAny(task, cancellationTask.Task).ConfigureAwait(false))
        {
            throw new OperationCanceledException(cancellationToken);
        }

        await task.ConfigureAwait(false);
    }

    public static async Task<T> WaitAsync<T>(Task<T> task, CancellationToken cancellationToken)
    {
        if (task.IsCompleted)
        {
            return await task.ConfigureAwait(false);
        }

        var cancellationTask = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<object?>)state!).TrySetResult(null),
            cancellationTask);

        if (task != await Task.WhenAny(task, cancellationTask.Task).ConfigureAwait(false))
        {
            throw new OperationCanceledException(cancellationToken);
        }

        return await task.ConfigureAwait(false);
    }

    public static Task<string> ReadToEndAsync(StreamReader reader, CancellationToken cancellationToken)
    {
#if NETSTANDARD2_0
        return WaitAsync(reader.ReadToEndAsync(), cancellationToken);
#else
        return reader.ReadToEndAsync(cancellationToken);
#endif
    }

    public static async Task<T> WithRetryAsync<T>(
        Func<Task<T>> action,
        Action<Exception> onFailedTry,
        int maxRetries,
        TimeSpan baseDelay,
        TimeSpan maxDelay,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastException = exception;
                if (attempt == maxRetries)
                {
                    break;
                }

                onFailedTry(exception);
                var exponent = Math.Pow(2, attempt);
                var jitter = 0.8 + (RandomHelpers.NextDouble() * 0.4);
                var delayMs = Math.Min(maxDelay.TotalMilliseconds, baseDelay.TotalMilliseconds * exponent * jitter);
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken).ConfigureAwait(false);
            }
        }

        throw lastException ?? new InvalidOperationException("Retry operation failed without an exception.");
    }
}
