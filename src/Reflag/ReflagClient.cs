using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Reflag.Internal;

namespace Reflag;

public sealed class ReflagClient : IAsyncDisposable
{
    private static readonly ReflagEvaluationError ClientNotInitializedError = new()
    {
        Code = "CLIENT_NOT_INITIALIZED",
        Field = string.Empty,
        Message = "ReflagClient was not initialized before this flag was evaluated. Call InitializeAsync() before evaluating flags.",
    };

    private readonly object _initializeGate = new();
    private readonly object _overridesGate = new();
    private readonly CancellationTokenSource _disposeCancellationTokenSource = new();
    private readonly ClientConfig _config;
    private readonly ILogger _logger;
    private readonly HttpClientTransport _transport;
    private readonly FlagsCache _flagsCache;
    private readonly IFlagsSyncController _flagsSyncController;
    private readonly FlagsFallbackProviderContext _flagsFallbackProviderContext;
    private readonly BatchBuffer<BulkItem> _batchBuffer;
    private readonly RateLimiter _rateLimiter;
    private readonly bool _flushOnExit;

    private static readonly IReadOnlyDictionary<string, JsonElement> EmptyOverrides =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    private readonly object _fallbackSaveGate = new();
    private Func<ReflagContext, IReadOnlyDictionary<string, JsonElement>> _baseFlagOverrides = static _ => EmptyOverrides;
    private Func<ReflagContext, IReadOnlyDictionary<string, JsonElement>> _effectiveFlagOverrides = static _ => EmptyOverrides;
    private Task _fallbackSaveTask = Task.CompletedTask;
    private readonly List<FlagOverrideLayer> _flagOverrideLayers = new();
    private int _nextFlagOverrideLayerId;
    private volatile bool _initializationFinished;
    private Task? _initializeTask;
    private bool _disposed;
    private bool _shutdownFlushCompleted;
    private bool _canLoadFlagsFallbackProvider = true;

    public ReflagClient(ReflagClientOptions? options = null)
    {
        options ??= new ReflagClientOptions();
        ValidateOptions(options);

        var envConfig = LoadEnvironmentConfig();
        var offline = options.Offline ?? envConfig.Offline ?? false;
        var secretKey = options.SecretKey ?? envConfig.SecretKey;
        if (!offline)
        {
            if (secretKey is null)
            {
                throw new ArgumentNullException(nameof(options), "options.SecretKey must be provided unless offline is true.");
            }

            if (secretKey.Length <= 22)
            {
                throw new ArgumentException("invalid options.SecretKey specified", nameof(options));
            }
        }

        var apiBaseUrl = NormalizeBaseUrl(options.ApiBaseUrl ?? envConfig.ApiBaseUrl ?? new Uri(ReflagConstants.ApiBaseUrl));
        var flagsFetchRetries = options.FlagsFetchRetries ?? 3;
        var fetchTimeout = options.FetchTimeout ?? ReflagConstants.ApiTimeout;
        ThrowHelpers.ThrowIfNegative(flagsFetchRetries, nameof(options.FlagsFetchRetries));
        ThrowHelpers.ThrowIfNegative(fetchTimeout, nameof(options.FetchTimeout));

        var syncMode = options.FlagsSyncMode ?? ReflagFlagsSyncMode.Push;
        var batchMaxSize = options.Batch?.MaxSize ?? ReflagConstants.BatchMaxSize;
        var batchInterval = options.Batch?.Interval ?? ReflagConstants.BatchInterval;
        _flushOnExit = options.Batch?.FlushOnExit ?? true;
        ThrowHelpers.ThrowIfNegative(batchInterval, nameof(options.Batch));
        if (batchMaxSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "options.Batch.MaxSize must be greater than zero.");
        }

        var secretKeyHash = secretKey is null ? string.Empty : Hashing.HashString(secretKey);
        var flagsPushUrl = ResolveFlagsPushUrl(options.FlagsPushUrl);
        _logger = options.Logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        _transport = new HttpClientTransport(options.HttpClient, ownsHttpClient: options.HttpClient is null);

        _flagsFallbackProviderContext = new FlagsFallbackProviderContext
        {
            SecretKeyHash = secretKeyHash,
        };

        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Content-Type"] = "application/json",
            [ReflagConstants.SdkVersionHeaderName] = ReflagConstants.SdkVersion,
        };

        if (!string.IsNullOrEmpty(secretKey))
        {
            headers["Authorization"] = $"Bearer {secretKey}";
        }

        _config = new ClientConfig(
            offline,
            apiBaseUrl,
            headers,
            fetchTimeout,
            flagsFetchRetries,
            syncMode,
            flagsPushUrl,
            options.FlagsFallbackProvider);

        _baseFlagOverrides = BuildBaseFlagOverrides(envConfig.FlagOverrides, options.FlagOverrides, options.FlagOverridesFactory);
        _effectiveFlagOverrides = _baseFlagOverrides;

        _rateLimiter = new RateLimiter(ReflagConstants.FlagEventRateLimiterWindow);
        _batchBuffer = new BatchBuffer<BulkItem>(SendBulkItemsAsync, _logger, batchMaxSize, batchInterval);
        _flagsCache = new FlagsCache(FetchDefinitionsAsync, _logger, ReflagConstants.MinRefreshInterval);
        _flagsSyncController = syncMode switch
        {
            ReflagFlagsSyncMode.Polling => new PollingFlagsSyncController(_flagsCache, _logger, ReflagConstants.FlagsRefetchInterval),
            ReflagFlagsSyncMode.Push => new PushFlagsSyncController(
                _flagsCache,
                _logger,
                _config.FlagsPushUrl,
                _config.Headers,
                _transport),
            _ => new NoopFlagsSyncController(),
        };
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var initializeTask = GetOrCreateInitializeTask();
        return AsyncHelpers.WaitAsync(initializeTask, cancellationToken);
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        return FlushCoreAsync(cancellationToken);
    }

    public async Task RefreshFlagsAsync(int? waitForVersion = null, CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        if (waitForVersion is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(waitForVersion), "waitForVersion must be greater than or equal to zero.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_config.Offline)
        {
            return;
        }

        await _flagsCache.RefreshAsync(waitForVersion, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns the complete cached collection, including segments needed for local evaluation.</summary>
    public FlagDefinitions GetFlagDefinitions()
    {
        EnsureNotDisposed();
        _flagsSyncController.OnAccess();
        return _flagsCache.Get()?.Definitions ?? new FlagDefinitions();
    }

    /// <summary>
    /// Reads a Boolean flag. Missing, unresolved and non-Boolean values return <see langword="false" />.
    /// Registers a check event unless telemetry is disabled.
    /// </summary>
    public bool GetFlag(
        string key,
        ReflagContext context,
        ReflagTelemetryOptions? telemetryOptions = null)
    {
        return GetFlag(key, context, false, telemetryOptions);
    }

    /// <summary>
    /// Reads a flag value as <typeparamref name="T" />, for example a string, number, record or <c>JsonElement</c>.
    /// Returns <paramref name="defaultValue" /> when the flag is missing or unresolved, or when its JSON value
    /// cannot be read as <typeparamref name="T" /> (a warning is logged). JSON <c>null</c> is only returned for
    /// arbitrary-JSON types such as <c>JsonElement</c>, <c>JsonNode</c> or <see cref="object" />.
    /// Registers a check event unless telemetry is disabled.
    /// </summary>
    public T GetFlag<T>(
        string key,
        ReflagContext context,
        T defaultValue,
        ReflagTelemetryOptions? telemetryOptions = null)
    {
        EnsureNotDisposed();
        ThrowHelpers.ThrowIfNullOrWhitespace(key, nameof(key));
        var normalizedContext = ReflagContextNormalizer.NormalizeTypedContext(context);
        var normalizedTelemetry = NormalizeTelemetryOptions(telemetryOptions);

        var evaluation = EvaluateFlags(normalizedContext, normalizedTelemetry, key);
        var result = evaluation.Flags[key];
        WarnFlagEvaluationDiagnostics(result);

        var usedDefault = true;
        var value = defaultValue;
        if (result.Value is { } element)
        {
            if (FlagValueConversion.TryConvert<T>(element, out var converted))
            {
                value = converted;
                usedDefault = false;
            }
            else
            {
                _logger.LogWarning(
                    "flag \"{FlagKey}\" returned {ActualType}; expected {ExpectedType}. Using the default value.",
                    key,
                    FlagValueConversion.DescribeKind(element.ValueKind),
                    typeof(T).Name);
            }
        }

        // A default is not an authored selection; keep only the diagnostics.
        var checkResult = usedDefault
            ? new RawReflagFlag { FlagKey = key, RuleResults = result.RuleResults, Errors = result.Errors }
            : result;
        TryQueueCheckEvent(normalizedContext, normalizedTelemetry, checkResult, evaluation);
        return value;
    }

    /// <summary>
    /// Evaluates all flags and returns their JSON values (<see langword="null" /> when no value is available).
    /// Registers a check event for every returned flag unless telemetry is disabled.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement?> GetFlags(
        ReflagContext context,
        ReflagTelemetryOptions? telemetryOptions = null)
    {
        EnsureNotDisposed();
        var normalizedContext = ReflagContextNormalizer.NormalizeTypedContext(context);
        var normalizedTelemetry = NormalizeTelemetryOptions(telemetryOptions);

        var evaluation = EvaluateFlags(normalizedContext, normalizedTelemetry, null);
        var values = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        foreach (var (key, flag) in evaluation.Flags)
        {
            WarnFlagEvaluationDiagnostics(flag);
            TryQueueCheckEvent(normalizedContext, normalizedTelemetry, flag, evaluation);
            values[key] = flag.Value;
        }

        return values;
    }

    /// <summary>
    /// Evaluates all flags for a client SDK bootstrap payload (protocol v2). Serialization is not a read,
    /// so no check events are registered.
    /// </summary>
    public ReflagBootstrappedFlags GetFlagsForBootstrap(
        ReflagContext context,
        ReflagTelemetryOptions? telemetryOptions = null)
    {
        EnsureNotDisposed();
        var normalizedContext = ReflagContextNormalizer.NormalizeTypedContext(context);
        var normalizedTelemetry = NormalizeTelemetryOptions(telemetryOptions);

        var evaluation = EvaluateFlags(normalizedContext, normalizedTelemetry, null);
        return new ReflagBootstrappedFlags
        {
            Context = normalizedContext,
            Flags = evaluation.Flags,
            FlagStateVersion = evaluation.FlagStateVersion,
        };
    }

    public ReflagBoundClient BindClient(
        ReflagContext context,
        ReflagTelemetryOptions? telemetryOptions = null)
    {
        EnsureNotDisposed();
        var normalizedContext = ReflagContextNormalizer.NormalizeTypedContext(context);
        var normalizedTelemetry = NormalizeTelemetryOptions(telemetryOptions);
        return CreateBoundClient(normalizedContext, normalizedTelemetry);
    }

    public ReflagBoundClient BindClient(
        object context,
        ReflagTelemetryOptions? telemetryOptions = null)
    {
        EnsureNotDisposed();
        var normalizedContext = ReflagContextNormalizer.NormalizeLooseContext(context);
        var normalizedTelemetry = NormalizeTelemetryOptions(telemetryOptions);
        return CreateBoundClient(normalizedContext, normalizedTelemetry);
    }

    public async Task UpdateUserAsync(
        string userId,
        ReflagTrackOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        ValidateId(userId, nameof(userId));
        ValidateTrackOptions(options);
        cancellationToken.ThrowIfCancellationRequested();

        if (_config.Offline)
        {
            return;
        }

        var dedupeKey = HashObjectSerializer.HashObject(new Dictionary<string, object?>
        {
            ["userId"] = userId,
            ["attributes"] = options?.Attributes is null ? null : CollectionHelpers.ToDictionary(options.Attributes, StringComparer.Ordinal),
            ["active"] = options?.Active,
        });

        if (!_rateLimiter.IsAllowed(dedupeKey))
        {
            return;
        }

        await _batchBuffer.AddAsync(
            new UserBulkItem
            {
                UserId = userId,
                Attributes = options?.Attributes is null ? null : CollectionHelpers.ToDictionary(options.Attributes, StringComparer.Ordinal),
                Context = options?.Active is null ? null : new BulkContext { Active = options.Active },
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateCompanyAsync(
        string companyId,
        ReflagCompanyTrackOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        ValidateId(companyId, nameof(companyId));
        ValidateTrackOptions(options);
        if (options?.UserId is not null)
        {
            ValidateId(options.UserId, nameof(options.UserId));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_config.Offline)
        {
            return;
        }

        var dedupeKey = HashObjectSerializer.HashObject(new Dictionary<string, object?>
        {
            ["companyId"] = companyId,
            ["userId"] = options?.UserId,
            ["attributes"] = options?.Attributes is null ? null : CollectionHelpers.ToDictionary(options.Attributes, StringComparer.Ordinal),
            ["active"] = options?.Active,
        });

        if (!_rateLimiter.IsAllowed(dedupeKey))
        {
            return;
        }

        await _batchBuffer.AddAsync(
            new CompanyBulkItem
            {
                CompanyId = companyId,
                UserId = options?.UserId,
                Attributes = options?.Attributes is null ? null : CollectionHelpers.ToDictionary(options.Attributes, StringComparer.Ordinal),
                Context = options?.Active is null ? null : new BulkContext { Active = options.Active },
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task TrackAsync(
        string userId,
        string eventName,
        ReflagEventTrackOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        ValidateId(userId, nameof(userId));
        ThrowHelpers.ThrowIfNullOrWhitespace(eventName, nameof(eventName));
        ValidateTrackOptions(options);
        if (options?.CompanyId is not null)
        {
            ValidateId(options.CompanyId, nameof(options.CompanyId));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_config.Offline)
        {
            return;
        }

        await _batchBuffer.AddAsync(
            new EventBulkItem
            {
                UserId = userId,
                Event = eventName,
                CompanyId = options?.CompanyId,
                Attributes = options?.Attributes is null ? null : CollectionHelpers.ToDictionary(options.Attributes, StringComparer.Ordinal),
                Context = options?.Active is null ? null : new BulkContext { Active = options.Active },
            },
            cancellationToken).ConfigureAwait(false);
    }

    public void SetFlagOverrides(IReadOnlyDictionary<string, bool> overrides)
    {
        EnsureNotDisposed();
        ThrowHelpers.ThrowIfNull(overrides, nameof(overrides));
        var normalized = FlagValueConversion.ToJsonOverrides(overrides);
        SetFlagOverridesCore(_ => normalized);
    }

    /// <summary>Overrides flags with arbitrary JSON-serializable values.</summary>
    public void SetFlagOverrides(IReadOnlyDictionary<string, object?> overrides)
    {
        EnsureNotDisposed();
        ThrowHelpers.ThrowIfNull(overrides, nameof(overrides));
        var normalized = FlagValueConversion.ToJsonOverrides(overrides);
        SetFlagOverridesCore(_ => normalized);
    }

    public void SetFlagOverrides(Func<ReflagContext, IReadOnlyDictionary<string, bool>> overridesFactory)
    {
        EnsureNotDisposed();
        ThrowHelpers.ThrowIfNull(overridesFactory, nameof(overridesFactory));
        SetFlagOverridesCore(FlagValueConversion.ToJsonOverridesFactory(overridesFactory));
    }

    /// <summary>Overrides flags with arbitrary JSON-serializable values computed per context.</summary>
    public void SetFlagOverrides(Func<ReflagContext, IReadOnlyDictionary<string, object?>> overridesFactory)
    {
        EnsureNotDisposed();
        ThrowHelpers.ThrowIfNull(overridesFactory, nameof(overridesFactory));
        SetFlagOverridesCore(FlagValueConversion.ToJsonOverridesFactory(overridesFactory));
    }

    public IDisposable PushFlagOverrides(IReadOnlyDictionary<string, bool> overrides)
    {
        EnsureNotDisposed();
        ThrowHelpers.ThrowIfNull(overrides, nameof(overrides));
        var normalized = FlagValueConversion.ToJsonOverrides(overrides);
        return PushFlagOverridesCore(_ => normalized);
    }

    /// <summary>Pushes an override layer with arbitrary JSON-serializable values.</summary>
    public IDisposable PushFlagOverrides(IReadOnlyDictionary<string, object?> overrides)
    {
        EnsureNotDisposed();
        ThrowHelpers.ThrowIfNull(overrides, nameof(overrides));
        var normalized = FlagValueConversion.ToJsonOverrides(overrides);
        return PushFlagOverridesCore(_ => normalized);
    }

    public IDisposable PushFlagOverrides(Func<ReflagContext, IReadOnlyDictionary<string, bool>> overridesFactory)
    {
        EnsureNotDisposed();
        ThrowHelpers.ThrowIfNull(overridesFactory, nameof(overridesFactory));
        return PushFlagOverridesCore(FlagValueConversion.ToJsonOverridesFactory(overridesFactory));
    }

    /// <summary>Pushes an override layer with arbitrary JSON-serializable values computed per context.</summary>
    public IDisposable PushFlagOverrides(Func<ReflagContext, IReadOnlyDictionary<string, object?>> overridesFactory)
    {
        EnsureNotDisposed();
        ThrowHelpers.ThrowIfNull(overridesFactory, nameof(overridesFactory));
        return PushFlagOverridesCore(FlagValueConversion.ToJsonOverridesFactory(overridesFactory));
    }

    public void ClearFlagOverrides()
    {
        EnsureNotDisposed();
        lock (_overridesGate)
        {
            _baseFlagOverrides = static _ => EmptyOverrides;
            SyncFlagOverridesNoLock();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _flagsSyncController.DisposeAsync().ConfigureAwait(false);
        using (var shutdownFlushCancellationTokenSource = new CancellationTokenSource(ReflagConstants.EndFlushTimeout))
        {
            await FlushOnShutdownAsync(shutdownFlushCancellationTokenSource.Token).ConfigureAwait(false);
        }

        _disposeCancellationTokenSource.Cancel();
        _flagsCache.Destroy();
        _batchBuffer.Destroy();
        _transport.Dispose();
        _disposeCancellationTokenSource.Dispose();
    }

    internal ReflagBoundClient CreateBoundClient(ReflagContext context, ReflagTelemetryOptions? telemetryOptions)
    {
        var boundClient = new ReflagBoundClient(this, context, telemetryOptions);
        _ = SyncContextAsync(context, telemetryOptions);
        return boundClient;
    }

    internal Task TrackBoundClientAsync(
        ReflagContext context,
        ReflagTelemetryOptions? telemetryOptions,
        string eventName,
        ReflagEventTrackOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        if (telemetryOptions?.EnableTelemetry == false)
        {
            LogBoundClientTelemetryDisabled();
            return Task.CompletedTask;
        }

        var userId = context.User?.Id;
        if (string.IsNullOrWhiteSpace(userId))
        {
            LogBoundClientMissingUser();
            return Task.CompletedTask;
        }

        var trackOptions = options is null
            ? new ReflagEventTrackOptions
            {
                CompanyId = context.Company?.Id,
            }
            : new ReflagEventTrackOptions
            {
                CompanyId = options.CompanyId ?? context.Company?.Id,
                Attributes = options.Attributes,
                Active = options.Active,
            };

        return TrackAsync(userId!, eventName, trackOptions, cancellationToken);
    }

    internal ReflagBoundClient BindBoundClient(
        ReflagContext existingContext,
        ReflagTelemetryOptions? existingTelemetryOptions,
        ReflagContext updateContext,
        ReflagTelemetryOptions? updateTelemetryOptions)
    {
        EnsureNotDisposed();
        var normalizedContext = ReflagContextNormalizer.NormalizeTypedContext(updateContext);
        var normalizedTelemetry = NormalizeTelemetryOptions(updateTelemetryOptions);
        return Rebind(existingContext, existingTelemetryOptions, normalizedContext, normalizedTelemetry);
    }

    internal ReflagBoundClient BindBoundClient(
        ReflagContext existingContext,
        ReflagTelemetryOptions? existingTelemetryOptions,
        object updateContext,
        ReflagTelemetryOptions? updateTelemetryOptions)
    {
        EnsureNotDisposed();
        var normalizedContext = ReflagContextNormalizer.NormalizeLooseContext(updateContext);
        var normalizedTelemetry = NormalizeTelemetryOptions(updateTelemetryOptions);
        return Rebind(existingContext, existingTelemetryOptions, normalizedContext, normalizedTelemetry);
    }

    internal ReflagBoundClient Rebind(
        ReflagContext existingContext,
        ReflagTelemetryOptions? existingTelemetryOptions,
        ReflagContext updateContext,
        ReflagTelemetryOptions? updateTelemetryOptions)
    {
        var mergedContext = ReflagContextNormalizer.MergeBoundContext(existingContext, updateContext);
        var telemetry = updateTelemetryOptions ?? existingTelemetryOptions;
        return CreateBoundClient(mergedContext, telemetry);
    }

    internal async Task FlushOnShutdownAsync(CancellationToken cancellationToken)
    {
        if (_config.Offline || !_flushOnExit || _shutdownFlushCompleted)
        {
            return;
        }

        try
        {
            await FlushCoreAsync(cancellationToken).ConfigureAwait(false);
            _shutdownFlushCompleted = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("shutdown flush was canceled before completion");
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "failed to flush buffered events during shutdown");
        }
    }

    private void SetFlagOverridesCore(Func<ReflagContext, IReadOnlyDictionary<string, JsonElement>> overridesFactory)
    {
        lock (_overridesGate)
        {
            _baseFlagOverrides = NormalizeFlagOverrides(overridesFactory);
            SyncFlagOverridesNoLock();
        }
    }

    private IDisposable PushFlagOverridesCore(Func<ReflagContext, IReadOnlyDictionary<string, JsonElement>> overridesFactory)
    {
        FlagOverrideLayer layer;
        lock (_overridesGate)
        {
            layer = new FlagOverrideLayer(_nextFlagOverrideLayerId++, NormalizeFlagOverrides(overridesFactory));
            _flagOverrideLayers.Add(layer);
            SyncFlagOverridesNoLock();
        }

        return new FlagOverrideScope(this, layer.Id);
    }

    private Task GetOrCreateInitializeTask()
    {
        lock (_initializeGate)
        {
            return _initializeTask ??= InitializeCoreAsync();
        }
    }

    private async Task FlushCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_config.Offline)
        {
            return;
        }

        await _batchBuffer.FlushAsync(cancellationToken).ConfigureAwait(false);
        await _flagsCache.WaitForRefreshAsync(cancellationToken).ConfigureAwait(false);
        Task fallbackSaveTask;
        lock (_fallbackSaveGate)
        {
            fallbackSaveTask = _fallbackSaveTask;
        }

        await AsyncHelpers.WaitAsync(fallbackSaveTask, cancellationToken).ConfigureAwait(false);
    }

    private async Task InitializeCoreAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        if (!_config.Offline)
        {
            await _flagsSyncController.StartAsync().ConfigureAwait(false);
            await _flagsCache.RefreshAsync(null, CancellationToken.None).ConfigureAwait(false);
        }

        stopwatch.Stop();
        if (_config.Offline)
        {
            _logger.LogInformation("Reflag initialized in {ElapsedMilliseconds}ms (offline mode)", stopwatch.ElapsedMilliseconds);
        }
        else
        {
            _logger.LogInformation("Reflag initialized in {ElapsedMilliseconds}ms", stopwatch.ElapsedMilliseconds);
        }

        _initializationFinished = true;
    }

    private PreparedFlagCollection? GetCollectionForLocalEvaluation()
    {
        if (_config.Offline)
        {
            return null;
        }

        _flagsSyncController.OnAccess();
        var collection = _flagsCache.Get();
        if (collection is null)
        {
            _logger.LogWarning("no flag definitions available, using flag overrides only.");
        }

        return collection;
    }

    private FlagsEvaluation EvaluateFlags(
        ReflagContext context,
        ReflagTelemetryOptions? telemetryOptions,
        string? key)
    {
        var evaluatedBeforeInitialization = !_initializationFinished && !_config.Offline;
        if (evaluatedBeforeInitialization)
        {
            _logger.LogError("flag access: ReflagClient is not initialized yet.");
        }

        _ = SyncContextAsync(context, telemetryOptions);
        var collection = GetCollectionForLocalEvaluation();
        var flagStateVersion = _flagsCache.GetFlagStateVersion();
        var flags = new Dictionary<string, RawReflagFlag>(StringComparer.Ordinal);
        if (collection is not null)
        {
            var evaluationObject = ReflagContextNormalizer.ToEvaluationObject(context);
            if (key is null)
            {
                foreach (var (flagKey, result) in collection.Evaluator.EvaluateAll(evaluationObject))
                {
                    flags[flagKey] = result;
                }
            }
            else if (collection.Evaluator.HasFlag(key))
            {
                flags[key] = collection.Evaluator.EvaluateFlag(evaluationObject, key);
            }

            if (!collection.TrackSelections)
            {
                foreach (var flagKey in flags.Keys.ToArray())
                {
                    flags[flagKey] = LocalResult(flagKey, flags[flagKey].Value);
                }
            }
        }

        // Overrides apply after evaluation; they do not change the results of dependent flags.
        foreach (var (flagKey, value) in GetFlagOverrides(context))
        {
            if (key is null || string.Equals(key, flagKey, StringComparison.Ordinal))
            {
                flags[flagKey] = LocalResult(flagKey, value);
            }
        }

        if (key is not null && !flags.ContainsKey(key))
        {
            flags[key] = LocalResult(key, null);
        }

        return new FlagsEvaluation(flags, flagStateVersion, evaluatedBeforeInitialization);
    }

    private static RawReflagFlag LocalResult(string key, JsonElement? value)
    {
        return new RawReflagFlag
        {
            FlagKey = key,
            Resolved = false,
            Value = value,
        };
    }

    private IReadOnlyDictionary<string, JsonElement> GetFlagOverrides(ReflagContext context)
    {
        return _effectiveFlagOverrides(context) ?? EmptyOverrides;
    }

    private async Task<FlagsCacheRefreshResult?> FetchDefinitionsAsync(int? waitForVersion)
    {
        var path = waitForVersion is null
            ? "flags"
            : $"flags?waitForVersion={Uri.EscapeDataString(waitForVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))}";

        var response = await GetAsync<GetFlagsResponse>(path).ConfigureAwait(false);
        if (response is { ProtocolVersion: 2, FlagStateVersion: >= 0 })
        {
            try
            {
                var collection = PreparedFlagCollection.Prepare(
                    DeserializeCollectionPart<FlagDefinition>(response.Flags),
                    DeserializeCollectionPart<SegmentDefinition>(response.Segments));
                _canLoadFlagsFallbackProvider = false;
                var flagStateVersion = response.FlagStateVersion.Value;
                if (flagStateVersion < (_flagsCache.GetFlagStateVersion() ?? -1))
                {
                    return null;
                }

                QueueFlagsFallbackSave(collection, flagStateVersion);
                return new FlagsCacheRefreshResult(collection, flagStateVersion);
            }
            catch (Exception error) when (error is ArgumentException or JsonException or NotSupportedException or InvalidOperationException)
            {
                _logger.LogDebug(error, "flag collection from '{Path}' is invalid", path);
            }
        }
        else if (response is not null)
        {
            _logger.LogDebug("unsupported flag collection response from '{Path}'", path);
        }

        return await LoadFlagsFallbackDefinitionsAsync().ConfigureAwait(false);
    }

    private static IReadOnlyList<TDefinition>? DeserializeCollectionPart<TDefinition>(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Array } array)
        {
            return null;
        }

        return JsonSerializer.Deserialize<TDefinition[]>(array.GetRawText(), ReflagJson.Options);
    }

    private async Task<TResponse?> GetAsync<TResponse>(string path)
        where TResponse : class, ISuccessResponse
    {
        var url = BuildUrl(path);

        try
        {
            return await AsyncHelpers.WithRetryAsync(
                async () =>
                {
                    var response = await _transport.GetAsync<TResponse>(
                        url,
                        _config.Headers,
                        _config.FetchTimeout,
                        _disposeCancellationTokenSource.Token).ConfigureAwait(false);

                    _logger.LogDebug("get request to \"{Url}\" {Response}", url, response);
                    if (!response.IsSuccessStatusCode || response.Body is null || !response.Body.Success)
                    {
                        throw new InvalidOperationException(
                            $"invalid response received from server for '{url}'");
                    }

                    return response.Body;
                },
                error => _logger.LogWarning(error, "failed to fetch flags, will retry"),
                _config.FlagsFetchRetries,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(10),
                _disposeCancellationTokenSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_disposeCancellationTokenSource.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception error)
        {
            _logger.LogDebug(error, "get request to \"{Path}\" failed with error after {RetryCount} retries", path, _config.FlagsFetchRetries);
            return null;
        }
    }

    private async Task PostAsync<TRequest>(string path, TRequest body, CancellationToken cancellationToken)
    {
        var url = BuildUrl(path);

        var response = await _transport.PostAsync<TRequest, SuccessResponse>(
            url,
            _config.Headers,
            body,
            cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("post request to \"{Url}\" {Response}", url, response);
        if (!response.IsSuccessStatusCode || response.Body is null || !response.Body.Success)
        {
            throw new InvalidOperationException($"invalid response received from server for '{url}'");
        }
    }

    private Task SendBulkItemsAsync(IReadOnlyList<BulkItem> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return Task.CompletedTask;
        }

        return PostAsync("bulk", items.Cast<object>().ToArray(), cancellationToken);
    }

    private async Task<FlagsCacheRefreshResult?> LoadFlagsFallbackDefinitionsAsync()
    {
        if (!_canLoadFlagsFallbackProvider)
        {
            return null;
        }

        _canLoadFlagsFallbackProvider = false;
        if (_config.FlagsFallbackProvider is null)
        {
            return null;
        }

        try
        {
            var snapshot = await _config.FlagsFallbackProvider.LoadAsync(
                _flagsFallbackProviderContext,
                _disposeCancellationTokenSource.Token).ConfigureAwait(false);

            if (snapshot is null)
            {
                _logger.LogWarning("remote flags unavailable, no fallback flags found in flagsFallbackProvider");
                return null;
            }

            if (snapshot.SchemaVersion != FlagsFallbackSnapshot.CurrentSchemaVersion ||
                snapshot.Flags is null ||
                snapshot.Segments is null ||
                snapshot.FlagStateVersion is < 0 ||
                snapshot.Source is not (null or "static"))
            {
                // Schema version 1 snapshots cannot be evaluated by the collection evaluator.
                _logger.LogWarning("flagsFallbackProvider: invalid or unsupported snapshot returned");
                return null;
            }

            var collection = PreparedFlagCollection.Prepare(
                snapshot.Flags,
                snapshot.Segments,
                trackSelections: snapshot.Source is null);

            var age = FormatFallbackAge(snapshot.SavedAt);
            if (age is null)
            {
                _logger.LogWarning("remote flags unavailable, using fallback flags ({SavedAt:O})", snapshot.SavedAt);
            }
            else
            {
                _logger.LogWarning("remote flags unavailable, using fallback flags fetched {Age} ago ({SavedAt:O})", age, snapshot.SavedAt);
            }

            return new FlagsCacheRefreshResult(collection, snapshot.FlagStateVersion);
        }
        catch (OperationCanceledException) when (_disposeCancellationTokenSource.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception error)
        {
            _logger.LogError(error, "flagsFallbackProvider: failed to load flag definitions");
            return null;
        }
    }

    private void QueueFlagsFallbackSave(PreparedFlagCollection collection, int flagStateVersion)
    {
        if (_config.FlagsFallbackProvider is null)
        {
            return;
        }

        var snapshot = new FlagsFallbackSnapshot
        {
            SchemaVersion = FlagsFallbackSnapshot.CurrentSchemaVersion,
            SavedAt = DateTimeOffset.UtcNow,
            FlagStateVersion = flagStateVersion,
            Flags = collection.Definitions.Flags,
            Segments = collection.Definitions.Segments,
        };

        // Serialize saves so that an older, slower write cannot overwrite a newer collection.
        lock (_fallbackSaveGate)
        {
            _fallbackSaveTask = SaveFlagsFallbackDefinitionsAfterAsync(_fallbackSaveTask, snapshot);
        }
    }

    private async Task SaveFlagsFallbackDefinitionsAfterAsync(Task previousSave, FlagsFallbackSnapshot snapshot)
    {
        await previousSave.ConfigureAwait(false);
        try
        {
            await _config.FlagsFallbackProvider!.SaveAsync(
                _flagsFallbackProviderContext,
                snapshot,
                _disposeCancellationTokenSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_disposeCancellationTokenSource.IsCancellationRequested)
        {
            // no-op
        }
        catch (Exception error)
        {
            _logger.LogError(error, "flagsFallbackProvider: failed to save flag definitions");
        }
    }

    private Uri BuildUrl(string path)
    {
        var trimmedPath = path.StartsWith("/", StringComparison.Ordinal) ? path.Substring(1) : path;
        return new Uri(_config.ApiBaseUrl, trimmedPath);
    }

    private async Task SyncContextAsync(ReflagContext context, ReflagTelemetryOptions? telemetryOptions)
    {
        if (telemetryOptions?.EnableTelemetry == false)
        {
            _logger.LogDebug("telemetry disabled, not updating user/company");
            return;
        }

        try
        {
            var tasks = new List<Task>(2);
            var active = telemetryOptions?.Active;

            if (context.Company?.Id is not null)
            {
                tasks.Add(UpdateCompanyAsync(
                    context.Company.Id,
                    new ReflagCompanyTrackOptions
                    {
                        UserId = context.User?.Id,
                        Attributes = BuildCompanyAttributes(context.Company),
                        Active = active,
                    },
                    CancellationToken.None));
            }

            if (context.User?.Id is not null)
            {
                tasks.Add(UpdateUserAsync(
                    context.User.Id,
                    new ReflagTrackOptions
                    {
                        Attributes = BuildUserAttributes(context.User),
                        Active = active,
                    },
                    CancellationToken.None));
            }

            if (tasks.Count > 0)
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_disposed)
        {
            // no-op during disposal
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            // no-op during disposal
        }
    }

    private void WarnFlagEvaluationDiagnostics(RawReflagFlag flag)
    {
        if (flag.Errors.Count == 0)
        {
            return;
        }

        var warningKey = HashObjectSerializer.HashObject(new Dictionary<string, object?>
        {
            ["type"] = "evaluation-warning",
            ["flagKey"] = flag.FlagKey,
            ["errors"] = JsonSerializer.Serialize(flag.Errors
                .OrderBy(error => error.Code, StringComparer.Ordinal)
                .ThenBy(error => error.Field, StringComparer.Ordinal)
                .ThenBy(error => error.Operator, StringComparer.Ordinal)
                .ThenBy(error => error.Source?.Type, StringComparer.Ordinal)
                .ThenBy(error => error.Source?.Key, StringComparer.Ordinal)
                .Select(error => new[]
                {
                    error.Code,
                    error.Field ?? string.Empty,
                    error.Operator ?? string.Empty,
                    error.Source?.Type ?? string.Empty,
                    error.Source?.Key ?? string.Empty,
                })),
        });

        if (!_rateLimiter.IsAllowed(warningKey))
        {
            return;
        }

        _logger.LogWarning(
            "flag targeting rules could not be fully evaluated. {EvaluationErrors}",
            new Dictionary<string, IReadOnlyList<ReflagEvaluationError>>
            {
                [flag.FlagKey] = flag.Errors,
            });
    }

    private void TryQueueCheckEvent(
        ReflagContext context,
        ReflagTelemetryOptions? telemetryOptions,
        RawReflagFlag flag,
        FlagsEvaluation evaluation)
    {
        if (telemetryOptions?.EnableTelemetry == false || _config.Offline)
        {
            return;
        }

        _ = SendFlagCheckEventSafeAsync(context, flag, evaluation.FlagStateVersion, evaluation.EvaluatedBeforeInitialization);
    }

    private async Task SendFlagCheckEventSafeAsync(
        ReflagContext context,
        RawReflagFlag flag,
        int? flagStateVersion,
        bool evaluatedBeforeInitialization)
    {
        try
        {
            await SendFlagCheckEventAsync(context, flag, flagStateVersion, evaluatedBeforeInitialization, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_disposed)
        {
            // no-op during disposal
        }
        catch (Exception error)
        {
            _logger.LogError(error, "failed to send check event for \"{FlagKey}\"", flag.FlagKey);
        }
    }

    private async Task SendFlagCheckEventAsync(
        ReflagContext context,
        RawReflagFlag flag,
        int? flagStateVersion,
        bool evaluatedBeforeInitialization,
        CancellationToken cancellationToken)
    {
        var evaluationContext = ReflagContextNormalizer.ToEvaluationObject(context);
        var errors = evaluatedBeforeInitialization
            ? flag.Errors.Concat(new[] { ClientNotInitializedError }).ToArray()
            : flag.Errors;
        var ruleResults = flag.RuleResults.Select(rule => rule.Matched).ToArray();

        // A check records the selected variant key and version identity, never the JSON value.
        var variantKey = flag.Resolved ? flag.VariantKey : null;
        var sourceVersionId = flag.Resolved ? flag.SourceVersionId : null;
        var matchedRuleId = flag.Resolved ? flag.MatchedRuleId : null;

        // Equal JSON payloads from different variants or versions are distinct checks.
        var dedupeKey = HashObjectSerializer.HashObject(new Dictionary<string, object?>
        {
            ["type"] = "feature-flag-event",
            ["action"] = "check",
            ["key"] = flag.FlagKey,
            ["evalResult"] = variantKey,
            ["sourceVersionId"] = sourceVersionId,
            ["matchedRuleId"] = matchedRuleId,
            ["flagStateVersion"] = flagStateVersion,
            ["evalRuleResults"] = string.Join(",", ruleResults.Select(matched => matched ? "1" : "0")),
            ["evalErrors"] = JsonSerializer.Serialize(errors, ReflagJson.Options),
            ["contextKey"] = BuildContextKey(evaluationContext),
        });

        if (!_rateLimiter.IsAllowed(dedupeKey))
        {
            return;
        }

        await _batchBuffer.AddAsync(
            new FeatureFlagEventBulkItem
            {
                Action = "check",
                Key = flag.FlagKey,
                EvalResult = variantKey,
                SourceVersionId = sourceVersionId,
                MatchedRuleId = matchedRuleId,
                FlagStateVersion = flagStateVersion,
                EvalContext = evaluationContext,
                EvalRuleResults = ruleResults,
                EvalErrors = errors.Count > 0 ? errors : null,
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static string BuildContextKey(IReadOnlyDictionary<string, object?> context)
    {
        return CanonicalContextJson.Serialize(context);
    }

    private void SyncFlagOverridesNoLock()
    {
        var layers = _flagOverrideLayers.Select(layer => layer.Factory).ToArray();
        _effectiveFlagOverrides = context =>
        {
            var merged = CollectionHelpers.ToDictionary(_baseFlagOverrides(context), StringComparer.Ordinal);
            foreach (var layer in layers)
            {
                foreach (var (key, value) in layer(context) ?? EmptyOverrides)
                {
                    merged[key] = value;
                }
            }

            return merged;
        };
    }

    private void RemoveFlagOverrideLayer(int layerId)
    {
        lock (_overridesGate)
        {
            var index = _flagOverrideLayers.FindIndex(layer => layer.Id == layerId);
            if (index < 0)
            {
                return;
            }

            _flagOverrideLayers.RemoveAt(index);
            SyncFlagOverridesNoLock();
        }
    }

    internal void LogBoundClientTelemetryDisabled()
    {
        _logger.LogDebug("telemetry disabled for this bound client, not tracking event");
    }

    internal void LogBoundClientMissingUser()
    {
        _logger.LogWarning("no user set, cannot track event");
    }

    private void EnsureNotDisposed()
    {
#if NETSTANDARD2_0
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ReflagClient));
        }
#else
        ObjectDisposedException.ThrowIf(_disposed, nameof(ReflagClient));
#endif
    }

    private static ReflagTelemetryOptions? NormalizeTelemetryOptions(ReflagTelemetryOptions? telemetryOptions)
    {
        if (telemetryOptions is null)
        {
            return null;
        }

        return new ReflagTelemetryOptions
        {
            EnableTelemetry = telemetryOptions.EnableTelemetry,
            Active = telemetryOptions.Active,
        };
    }

    private static Func<ReflagContext, IReadOnlyDictionary<string, JsonElement>> BuildBaseFlagOverrides(
        IReadOnlyDictionary<string, bool> envOverrides,
        IReadOnlyDictionary<string, bool>? optionOverrides,
        Func<ReflagContext, IReadOnlyDictionary<string, bool>>? optionOverridesFactory)
    {
        var normalizedEnvOverrides = FlagValueConversion.ToJsonOverrides(envOverrides);
        if (optionOverridesFactory is not null)
        {
            var normalizedFactory = FlagValueConversion.ToJsonOverridesFactory(optionOverridesFactory);
            return context => MergeOverrides(normalizedEnvOverrides, normalizedFactory(context));
        }

        if (optionOverrides is not null)
        {
            var normalizedOverrides = MergeOverrides(normalizedEnvOverrides, FlagValueConversion.ToJsonOverrides(optionOverrides));
            return _ => normalizedOverrides;
        }

        if (normalizedEnvOverrides.Count == 0)
        {
            return static _ => EmptyOverrides;
        }

        return _ => normalizedEnvOverrides;
    }

    private static Func<ReflagContext, IReadOnlyDictionary<string, JsonElement>> NormalizeFlagOverrides(
        Func<ReflagContext, IReadOnlyDictionary<string, JsonElement>> factory)
    {
        return context => factory(context) ?? EmptyOverrides;
    }

    private static IReadOnlyDictionary<string, JsonElement> MergeOverrides(
        IReadOnlyDictionary<string, JsonElement> lowerPriority,
        IReadOnlyDictionary<string, JsonElement> higherPriority)
    {
        var merged = CollectionHelpers.ToDictionary(lowerPriority, StringComparer.Ordinal);
        foreach (var (key, value) in higherPriority)
        {
            merged[key] = value;
        }

        return merged;
    }

    private static IReadOnlyDictionary<string, object?> BuildUserAttributes(ReflagUserContext user)
    {
        var attributes = CollectionHelpers.ToDictionary(user.Attributes, StringComparer.Ordinal);
        attributes["name"] = user.Name;
        attributes["email"] = user.Email;
        attributes["avatar"] = user.Avatar;
        return attributes;
    }

    private static IReadOnlyDictionary<string, object?> BuildCompanyAttributes(ReflagCompanyContext company)
    {
        var attributes = CollectionHelpers.ToDictionary(company.Attributes, StringComparer.Ordinal);
        attributes["name"] = company.Name;
        attributes["avatar"] = company.Avatar;
        return attributes;
    }

    private static void ValidateId(string value, string paramName)
    {
        ThrowHelpers.ThrowIfNullOrWhitespace(value, paramName);
    }

    private static void ValidateTrackOptions(ReflagTrackOptions? options)
    {
        if (options?.Attributes is null)
        {
            return;
        }

        _ = CollectionHelpers.ToDictionary(options.Attributes, StringComparer.Ordinal);
    }

    private static void ValidateOptions(ReflagClientOptions options)
    {
        if (options.ApiBaseUrl is not null && !options.ApiBaseUrl.IsAbsoluteUri)
        {
            throw new ArgumentException("options.ApiBaseUrl must be an absolute URI.", nameof(options));
        }

        if (options.FlagsPushUrl is not null && !options.FlagsPushUrl.IsAbsoluteUri)
        {
            throw new ArgumentException("options.FlagsPushUrl must be an absolute URI.", nameof(options));
        }

        if (options.FlagOverrides is not null && options.FlagOverridesFactory is not null)
        {
            throw new ArgumentException("Specify either FlagOverrides or FlagOverridesFactory, but not both.", nameof(options));
        }
    }

    private static Uri NormalizeBaseUrl(Uri uri)
    {
        if (!uri.IsAbsoluteUri)
        {
            throw new ArgumentException("apiBaseUrl must be an absolute URI.", nameof(uri));
        }

        return uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal) ? uri : new Uri($"{uri.AbsoluteUri}/");
    }

    private static Uri ResolveFlagsPushUrl(Uri? flagsPushUrl)
    {
        var resolved = flagsPushUrl ?? new Uri(ReflagConstants.PubsubSseUrl);
        if (!resolved.IsAbsoluteUri)
        {
            throw new ArgumentException("flagsPushUrl must be an absolute URI.", nameof(flagsPushUrl));
        }

        return RemoveChannelsQueryParameter(resolved);
    }

    private static Uri RemoveChannelsQueryParameter(Uri url)
    {
        var filteredQueryParameters = QueryStringHelpers.Parse(url.Query)
            .Where(parameter => !string.Equals(parameter.Key, "channels", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var builder = new UriBuilder(url)
        {
            Query = string.Join("&", filteredQueryParameters.Select(parameter => $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value)}")),
        };

        return builder.Uri;
    }

    private static string? FormatFallbackAge(DateTimeOffset savedAt)
    {
        var age = DateTimeOffset.UtcNow - savedAt;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age < TimeSpan.FromMinutes(1))
        {
            return "<1m";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{Math.Floor(age.TotalMinutes)}m";
        }

        if (age < TimeSpan.FromDays(1))
        {
            return $"{Math.Floor(age.TotalHours)}h";
        }

        return $"{Math.Floor(age.TotalDays)}d";
    }

    private static EnvironmentConfig LoadEnvironmentConfig()
    {
        var apiBaseUrl = Environment.GetEnvironmentVariable("REFLAG_API_BASE_URL");
        var offline = Environment.GetEnvironmentVariable("REFLAG_OFFLINE");
        return new EnvironmentConfig(
            Environment.GetEnvironmentVariable("REFLAG_SECRET_KEY"),
            apiBaseUrl is null ? null : new Uri(apiBaseUrl, UriKind.Absolute),
            ParseFlagOverrides(
                Environment.GetEnvironmentVariable("REFLAG_FLAGS_ENABLED"),
                Environment.GetEnvironmentVariable("REFLAG_FLAGS_DISABLED")),
            offline is null ? null : offline is "true" or "on");
    }

    private static IReadOnlyDictionary<string, bool> ParseFlagOverrides(string? enabledFlags, string? disabledFlags)
    {
        var overrides = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(enabledFlags))
        {
            foreach (var key in SplitFlagKeys(enabledFlags!))
            {
                overrides[key] = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(disabledFlags))
        {
            foreach (var key in SplitFlagKeys(disabledFlags!))
            {
                overrides[key] = false;
            }
        }

        return overrides;
    }

    private static IEnumerable<string> SplitFlagKeys(string value)
    {
        return StringParsing.SplitAndTrim(value, StringParsing.CommaSeparator);
    }

    private abstract record BulkItem
    {
        [JsonPropertyName("type")]
        public abstract string Type { get; }
    }

    private sealed record BulkContext
    {
        [JsonPropertyName("active")]
        public bool? Active { get; init; }
    }

    private sealed record UserBulkItem : BulkItem
    {
        public override string Type => "user";

        [JsonPropertyName("userId")]
        public string UserId { get; init; } = string.Empty;

        [JsonPropertyName("attributes")]
        public IReadOnlyDictionary<string, object?>? Attributes { get; init; }

        [JsonPropertyName("context")]
        public BulkContext? Context { get; init; }
    }

    private sealed record CompanyBulkItem : BulkItem
    {
        public override string Type => "company";

        [JsonPropertyName("companyId")]
        public string CompanyId { get; init; } = string.Empty;

        [JsonPropertyName("userId")]
        public string? UserId { get; init; }

        [JsonPropertyName("attributes")]
        public IReadOnlyDictionary<string, object?>? Attributes { get; init; }

        [JsonPropertyName("context")]
        public BulkContext? Context { get; init; }
    }

    private sealed record EventBulkItem : BulkItem
    {
        public override string Type => "event";

        [JsonPropertyName("event")]
        public string Event { get; init; } = string.Empty;

        [JsonPropertyName("userId")]
        public string UserId { get; init; } = string.Empty;

        [JsonPropertyName("companyId")]
        public string? CompanyId { get; init; }

        [JsonPropertyName("attributes")]
        public IReadOnlyDictionary<string, object?>? Attributes { get; init; }

        [JsonPropertyName("context")]
        public BulkContext? Context { get; init; }
    }

    private sealed record FeatureFlagEventBulkItem : BulkItem
    {
        public override string Type => "feature-flag-event";

        [JsonPropertyName("action")]
        public string Action { get; init; } = string.Empty;

        [JsonPropertyName("key")]
        public string Key { get; init; } = string.Empty;

        /// <summary>The selected variant key; absent when no authored variant was selected.</summary>
        [JsonPropertyName("evalResult")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? EvalResult { get; init; }

        [JsonPropertyName("sourceVersionId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SourceVersionId { get; init; }

        [JsonPropertyName("matchedRuleId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? MatchedRuleId { get; init; }

        [JsonPropertyName("flagStateVersion")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? FlagStateVersion { get; init; }

        [JsonPropertyName("evalContext")]
        public IReadOnlyDictionary<string, object?>? EvalContext { get; init; }

        [JsonPropertyName("evalRuleResults")]
        public IReadOnlyList<bool>? EvalRuleResults { get; init; }

        [JsonPropertyName("evalErrors")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<ReflagEvaluationError>? EvalErrors { get; init; }
    }

    private readonly record struct FlagOverrideLayer(int Id, Func<ReflagContext, IReadOnlyDictionary<string, JsonElement>> Factory);

    private sealed record FlagsEvaluation(
        Dictionary<string, RawReflagFlag> Flags,
        int? FlagStateVersion,
        bool EvaluatedBeforeInitialization);

    private sealed class FlagOverrideScope(ReflagClient client, int layerId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            client.RemoveFlagOverrideLayer(layerId);
        }
    }

    private sealed record ClientConfig(
        bool Offline,
        Uri ApiBaseUrl,
        IReadOnlyDictionary<string, string> Headers,
        TimeSpan FetchTimeout,
        int FlagsFetchRetries,
        ReflagFlagsSyncMode FlagsSyncMode,
        Uri FlagsPushUrl,
        IFlagsFallbackProvider? FlagsFallbackProvider);

    private sealed record EnvironmentConfig(
        string? SecretKey,
        Uri? ApiBaseUrl,
        IReadOnlyDictionary<string, bool> FlagOverrides,
        bool? Offline);

    internal interface ISuccessResponse
    {
        bool Success { get; }
    }

    internal sealed class SuccessResponse : ISuccessResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; init; }
    }

    internal sealed class GetFlagsResponse : ISuccessResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; init; }

        [JsonPropertyName("protocolVersion")]
        public int? ProtocolVersion { get; init; }

        [JsonPropertyName("flagStateVersion")]
        public int? FlagStateVersion { get; init; }

        // Deserialized after the envelope so that malformed definitions fall back instead of being retried.
        [JsonPropertyName("flags")]
        public JsonElement? Flags { get; init; }

        [JsonPropertyName("segments")]
        public JsonElement? Segments { get; init; }
    }

    private interface IFlagsSyncController : IAsyncDisposable
    {
        Task StartAsync();

        void OnAccess();
    }

    private sealed class NoopFlagsSyncController : IFlagsSyncController
    {
        public Task StartAsync() => Task.CompletedTask;

        public void OnAccess()
        {
        }

        public ValueTask DisposeAsync() => default;
    }

    private sealed class PollingFlagsSyncController(
        FlagsCache cache,
        ILogger logger,
        TimeSpan interval) : IFlagsSyncController
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private Task? _loopTask;

        public Task StartAsync()
        {
            lock (_gate)
            {
                if (_loopTask is not null)
                {
                    return Task.CompletedTask;
                }

                _loopTask = RunAsync();
                return Task.CompletedTask;
            }
        }

        public void OnAccess()
        {
        }

        public async ValueTask DisposeAsync()
        {
            _cancellationTokenSource.Cancel();
            if (_loopTask is not null)
            {
                try
                {
                    await _loopTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // no-op
                }
            }

            _cancellationTokenSource.Dispose();
        }

        private async Task RunAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(interval, _cancellationTokenSource.Token).ConfigureAwait(false);
                    try
                    {
                        await cache.RefreshAsync(null, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception error)
                    {
                        logger.LogWarning(error, "background flag refresh failed");
                    }
                }
            }
            catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
            {
                // no-op
            }
        }
    }

    private sealed class PushFlagsSyncController(
        FlagsCache cache,
        ILogger logger,
        Uri pushUrl,
        IReadOnlyDictionary<string, string> headers,
        HttpClientTransport transport) : IFlagsSyncController
    {
        private readonly object _gate = new();
        private FlagUpdatesSseSubscription? _subscription;

        public Task StartAsync()
        {
            return StartCoreAsync();
        }

        public void OnAccess()
        {
        }

        public async ValueTask DisposeAsync()
        {
            FlagUpdatesSseSubscription? subscription;
            lock (_gate)
            {
                subscription = _subscription;
                _subscription = null;
            }

            if (subscription is not null)
            {
                await subscription.DisposeAsync().ConfigureAwait(false);
            }
        }

        private async Task StartCoreAsync()
        {
            FlagUpdatesSseSubscription subscription;
            lock (_gate)
            {
                _subscription ??= new FlagUpdatesSseSubscription(
                    pushUrl,
                    headers,
                    transport,
                    logger,
                    HandleFlagStateVersion,
                    HandleReconnect);
                subscription = _subscription;
            }

            await subscription.Ready.ConfigureAwait(false);
        }

        private void HandleFlagStateVersion(int flagStateVersion)
        {
            _ = RefreshAsync(flagStateVersion);
        }

        private void HandleReconnect()
        {
            _ = RefreshAsync(null);
        }

        private async Task RefreshAsync(int? waitForVersion)
        {
            try
            {
                await cache.RefreshAsync(waitForVersion, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "background flag refresh failed");
            }
        }
    }

}

public sealed class ReflagBoundClient
{
    private readonly ReflagClient _rootClient;

    internal ReflagBoundClient(
        ReflagClient rootClient,
        ReflagContext context,
        ReflagTelemetryOptions? telemetryOptions)
    {
        _rootClient = rootClient;
        Context = context;
        TelemetryOptions = telemetryOptions;
    }

    public ReflagContext Context { get; }

    public ReflagTelemetryOptions? TelemetryOptions { get; }

    public ReflagUserContext? User => Context.User;

    public ReflagCompanyContext? Company => Context.Company;

    public IReadOnlyDictionary<string, object?>? OtherContext => Context.Other;

    public bool GetFlag(string key)
    {
        return _rootClient.GetFlag(key, Context, TelemetryOptions);
    }

    public T GetFlag<T>(string key, T defaultValue)
    {
        return _rootClient.GetFlag(key, Context, defaultValue, TelemetryOptions);
    }

    public IReadOnlyDictionary<string, JsonElement?> GetFlags()
    {
        return _rootClient.GetFlags(Context, TelemetryOptions);
    }

    public ReflagBootstrappedFlags GetFlagsForBootstrap()
    {
        return _rootClient.GetFlagsForBootstrap(Context, TelemetryOptions);
    }

    public Task TrackAsync(
        string eventName,
        ReflagEventTrackOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return _rootClient.TrackBoundClientAsync(Context, TelemetryOptions, eventName, options, cancellationToken);
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        return _rootClient.FlushAsync(cancellationToken);
    }

    public Task RefreshFlagsAsync(int? waitForVersion = null, CancellationToken cancellationToken = default)
    {
        return _rootClient.RefreshFlagsAsync(waitForVersion, cancellationToken);
    }

    public ReflagBoundClient BindClient(ReflagContext context, ReflagTelemetryOptions? telemetryOptions = null)
    {
        return _rootClient.BindBoundClient(Context, TelemetryOptions, context, telemetryOptions);
    }

    public ReflagBoundClient BindClient(object context, ReflagTelemetryOptions? telemetryOptions = null)
    {
        return _rootClient.BindBoundClient(Context, TelemetryOptions, context, telemetryOptions);
    }
}
