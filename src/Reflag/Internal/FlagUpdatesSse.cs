using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Reflag.Internal;

internal sealed class FlagUpdatesSseSubscription : IAsyncDisposable
{
    private readonly Uri _url;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly HttpClientTransport _transport;
    private readonly ILogger _logger;
    private readonly Action<int> _onFlagStateVersion;
    private readonly Action? _onReconnect;
    private readonly TimeSpan _initialReconnectDelay;
    private readonly TimeSpan _maxReconnectDelay;
    private readonly TaskCompletionSource<object?> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Task _runTask;

    private TimeSpan _reconnectDelay;
    private bool _shouldNotifyReconnect;

    public FlagUpdatesSseSubscription(
        Uri url,
        IReadOnlyDictionary<string, string> headers,
        HttpClientTransport transport,
        ILogger? logger,
        Action<int> onFlagStateVersion,
        Action? onReconnect = null,
        TimeSpan? initialReconnectDelay = null,
        TimeSpan? maxReconnectDelay = null)
    {
        _url = url;
        _headers = headers;
        _transport = transport;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _onFlagStateVersion = onFlagStateVersion;
        _onReconnect = onReconnect;
        _initialReconnectDelay = initialReconnectDelay ?? ReflagConstants.SseInitialReconnectDelay;
        _maxReconnectDelay = maxReconnectDelay ?? ReflagConstants.SseMaxReconnectDelay;
        _reconnectDelay = _initialReconnectDelay;
        _runTask = RunAsync();
    }

    public Task Ready => _ready.Task;

    public async ValueTask DisposeAsync()
    {
        _cancellationTokenSource.Cancel();
        try
        {
            await _runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
        {
            // no-op
        }
        finally
        {
            _cancellationTokenSource.Dispose();
        }
    }

    private async Task RunAsync()
    {
        while (!_cancellationTokenSource.IsCancellationRequested)
        {
            await ConnectAndReadAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
            if (_cancellationTokenSource.IsCancellationRequested)
            {
                break;
            }

            _shouldNotifyReconnect = true;
            var delay = _reconnectDelay;
            var nextDelayMs = Math.Min(_maxReconnectDelay.TotalMilliseconds, _reconnectDelay.TotalMilliseconds * 2);
            _reconnectDelay = TimeSpan.FromMilliseconds(nextDelayMs);

            try
            {
                await Task.Delay(delay, _cancellationTokenSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
            {
                break;
            }
        }

        _ready.TrySetResult(null);
    }

    private async Task ConnectAndReadAsync(CancellationToken cancellationToken)
    {
        ReflagSseTransportResponse response;
        try
        {
            response = await _transport.OpenServerSentEventsAsync(
                _url,
                BuildHeaders(_headers),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "failed to connect to flag updates SSE endpoint");
            _ready.TrySetResult(null);
            return;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode || response.Stream is null)
            {
                var reason = response.ReasonPhrase ?? string.Empty;
                var message = $"{response.StatusCode} {reason}".TrimEnd();
                _logger.LogWarning("flag updates SSE endpoint returned an invalid response: {Message}", message);
                _ready.TrySetResult(null);
                return;
            }

            _reconnectDelay = _initialReconnectDelay;
            _logger.LogDebug("flag updates SSE connection established");
            _ready.TrySetResult(null);

            if (_shouldNotifyReconnect)
            {
                _shouldNotifyReconnect = false;
                try
                {
                    _onReconnect?.Invoke();
                }
                catch (Exception error)
                {
                    _logger.LogWarning(error, "failed to handle flag updates SSE reconnect");
                }
            }

            var reader = new StreamReader(response.Stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
            var charBuffer = new char[4096];
            var buffered = string.Empty;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var read = await AsyncHelpers.WaitAsync(reader.ReadAsync(charBuffer, 0, charBuffer.Length), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    buffered += new string(charBuffer, 0, read)
                        .Replace("\r\n", "\n")
                        .Replace('\r', '\n');

                    while (true)
                    {
                        var separatorIndex = buffered.IndexOf("\n\n", StringComparison.Ordinal);
                        if (separatorIndex < 0)
                        {
                            break;
                        }

                        var block = buffered.Substring(0, separatorIndex);
                        buffered = buffered.Substring(separatorIndex + 2);
                        ParseBlock(block);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                _logger.LogDebug(error, "flag updates SSE stream failed; reconnecting");
            }
            finally
            {
                reader.Dispose();
            }
        }
    }

    private void ParseBlock(string block)
    {
        var eventName = "message";
        var dataLines = new List<string>();

        foreach (var rawLine in block.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.Length == 0 || line[0] == ':')
            {
                continue;
            }

            var delimiterIndex = line.IndexOf(':');
            var field = delimiterIndex >= 0 ? line.Substring(0, delimiterIndex) : line;
            var value = delimiterIndex >= 0 ? line.Substring(delimiterIndex + 1) : string.Empty;
            if (value.Length > 0 && value[0] == ' ')
            {
                value = value.Substring(1);
            }

            switch (field)
            {
                case "event":
                    eventName = value;
                    break;
                case "data":
                    dataLines.Add(value);
                    break;
            }
        }

        if (!string.Equals(eventName, "message", StringComparison.Ordinal) || dataLines.Count == 0)
        {
            return;
        }

        ParsePayload(string.Join("\n", dataLines));
    }

    private void ParsePayload(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!root.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
            {
                return;
            }

            if (!string.Equals(nameElement.GetString(), "flags-updated", StringComparison.Ordinal))
            {
                return;
            }

            if (!root.TryGetProperty("data", out var dataElement))
            {
                return;
            }

            var flagStateVersion = ExtractFlagStateVersion(dataElement);
            if (flagStateVersion is >= 0)
            {
                _onFlagStateVersion(flagStateVersion.Value);
            }
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "failed to parse SSE message");
        }
    }

    private static int? ExtractFlagStateVersion(JsonElement dataElement)
    {
        if (dataElement.ValueKind == JsonValueKind.String)
        {
            var raw = dataElement.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            using var nestedDocument = JsonDocument.Parse(raw!);
            return ExtractFlagStateVersion(nestedDocument.RootElement);
        }

        if (dataElement.ValueKind != JsonValueKind.Object || !dataElement.TryGetProperty("flagStateVersion", out var versionElement))
        {
            return null;
        }

        return TryReadNonNegativeInteger(versionElement, out var version)
            ? version
            : null;
    }

    private static bool TryReadNonNegativeInteger(JsonElement element, out int value)
    {
        value = default;

        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetInt32(out value) && value >= 0;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= 0;
        }

        return false;
    }

    private static IReadOnlyDictionary<string, string> BuildHeaders(IReadOnlyDictionary<string, string> headers)
    {
        var merged = CollectionHelpers.ToDictionary(headers, StringComparer.Ordinal);
        merged["Accept"] = "text/event-stream";
        merged["Cache-Control"] = "no-cache";
        return merged;
    }

}
