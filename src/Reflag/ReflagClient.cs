using System.Collections;
using System.Diagnostics;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Reflag.Internal;

namespace Reflag;

public sealed class ReflagClient : IAsyncDisposable
{
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

    private Func<ReflagContext, IReadOnlyDictionary<string, bool>> _baseFlagOverrides = static _ => EmptyBooleanDictionary.Instance;
    private Func<ReflagContext, IReadOnlyDictionary<string, bool>> _effectiveFlagOverrides = static _ => EmptyBooleanDictionary.Instance;
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

    public IReadOnlyList<FlagDefinition> GetFlagDefinitions()
    {
        EnsureNotDisposed();
        _flagsSyncController.OnAccess();

        var definitions = _flagsCache.Get();
        if (definitions is null || definitions.Length == 0)
        {
            return Array.Empty<FlagDefinition>();
        }

        return definitions.Select(definition => definition.Definition).ToArray();
    }

    public bool GetFlag(
        string key,
        ReflagContext context,
        ReflagTelemetryOptions? telemetryOptions = null)
    {
        EnsureNotDisposed();
        ThrowHelpers.ThrowIfNullOrWhitespace(key, nameof(key));
        var normalizedContext = ReflagContextNormalizer.NormalizeTypedContext(context);
        var normalizedTelemetry = NormalizeTelemetryOptions(telemetryOptions);

        if (!_initializationFinished)
        {
            _logger.LogError("flag access: ReflagClient is not initialized yet.");
        }

        _ = SyncContextAsync(normalizedContext, normalizedTelemetry);
        var definitions = GetDefinitionsForLocalEvaluation();
        var rawFlag = EvaluateFlag(key, normalizedContext, definitions);

        WarnMissingFlagContextFields(normalizedContext, rawFlag);
        TryQueueCheckEvent(normalizedContext, normalizedTelemetry, rawFlag);
        return rawFlag.Value;
    }

    public ReflagBootstrappedFlags GetFlagsForBootstrap(
        ReflagContext context,
        ReflagTelemetryOptions? telemetryOptions = null)
    {
        EnsureNotDisposed();
        var normalizedContext = ReflagContextNormalizer.NormalizeTypedContext(context);
        var normalizedTelemetry = NormalizeTelemetryOptions(telemetryOptions);

        if (!_initializationFinished)
        {
            _logger.LogError("flag access: ReflagClient is not initialized yet.");
        }

        _ = SyncContextAsync(normalizedContext, normalizedTelemetry);
        var definitions = GetDefinitionsForLocalEvaluation();
        var flags = EvaluateFlagsForBootstrap(normalizedContext, definitions);

        return new ReflagBootstrappedFlags
        {
            Context = normalizedContext,
            Flags = flags,
            FlagStateVersion = _flagsCache.GetFlagStateVersion(),
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
        SetFlagOverridesCore(_ => CollectionHelpers.ToDictionary(overrides, StringComparer.Ordinal));
    }

    public void SetFlagOverrides(Func<ReflagContext, IReadOnlyDictionary<string, bool>> overridesFactory)
    {
        EnsureNotDisposed();
        ThrowHelpers.ThrowIfNull(overridesFactory, nameof(overridesFactory));
        SetFlagOverridesCore(overridesFactory);
    }

    public IDisposable PushFlagOverrides(IReadOnlyDictionary<string, bool> overrides)
    {
        EnsureNotDisposed();
        ThrowHelpers.ThrowIfNull(overrides, nameof(overrides));
        return PushFlagOverridesCore(_ => CollectionHelpers.ToDictionary(overrides, StringComparer.Ordinal));
    }

    public IDisposable PushFlagOverrides(Func<ReflagContext, IReadOnlyDictionary<string, bool>> overridesFactory)
    {
        EnsureNotDisposed();
        ThrowHelpers.ThrowIfNull(overridesFactory, nameof(overridesFactory));
        return PushFlagOverridesCore(overridesFactory);
    }

    public void ClearFlagOverrides()
    {
        EnsureNotDisposed();
        lock (_overridesGate)
        {
            _baseFlagOverrides = static _ => EmptyBooleanDictionary.Instance;
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

    private void SetFlagOverridesCore(Func<ReflagContext, IReadOnlyDictionary<string, bool>> overridesFactory)
    {
        lock (_overridesGate)
        {
            _baseFlagOverrides = NormalizeFlagOverrides(overridesFactory);
            SyncFlagOverridesNoLock();
        }
    }

    private IDisposable PushFlagOverridesCore(Func<ReflagContext, IReadOnlyDictionary<string, bool>> overridesFactory)
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

    private CompiledFlagDefinition[] GetDefinitionsForLocalEvaluation()
    {
        if (_config.Offline)
        {
            return Array.Empty<CompiledFlagDefinition>();
        }

        _flagsSyncController.OnAccess();
        var definitions = _flagsCache.Get();
        if (definitions is null)
        {
            _logger.LogWarning("no flag definitions available, using fallback flags.");
            return Array.Empty<CompiledFlagDefinition>();
        }

        return definitions;
    }

    private RawReflagFlag EvaluateFlag(
        string key,
        ReflagContext context,
        IReadOnlyList<CompiledFlagDefinition> definitions)
    {
        var evaluationObject = ReflagContextNormalizer.ToEvaluationObject(context);
        var definition = definitions.FirstOrDefault(item => string.Equals(item.Definition.Key, key, StringComparison.Ordinal));
        RawReflagFlag evaluatedFlag;

        if (definition is null)
        {
            evaluatedFlag = new RawReflagFlag
            {
                Key = key,
                Value = false,
            };
        }
        else
        {
            var result = definition.Evaluator(evaluationObject, definition.Definition.Key);
            evaluatedFlag = new RawReflagFlag
            {
                Key = definition.Definition.Key,
                Value = result.Value,
                TargetingVersion = definition.Definition.Targeting.Version,
                RuleEvaluationResults = result.RuleEvaluationResults,
                MissingContextFields = result.MissingContextFields,
            };
        }

        if (TryGetOverride(context, key, out var overriddenValue))
        {
            return new RawReflagFlag
            {
                Key = key,
                Value = overriddenValue,
            };
        }

        return evaluatedFlag;
    }

    private IReadOnlyDictionary<string, RawReflagFlag> EvaluateFlagsForBootstrap(
        ReflagContext context,
        IReadOnlyList<CompiledFlagDefinition> definitions)
    {
        var evaluationObject = ReflagContextNormalizer.ToEvaluationObject(context);
        var result = new Dictionary<string, RawReflagFlag>(StringComparer.Ordinal);

        foreach (var definition in definitions)
        {
            var evaluation = definition.Evaluator(evaluationObject, definition.Definition.Key);
            result[definition.Definition.Key] = new RawReflagFlag
            {
                Key = definition.Definition.Key,
                Value = evaluation.Value,
                TargetingVersion = definition.Definition.Targeting.Version,
                RuleEvaluationResults = evaluation.RuleEvaluationResults,
                MissingContextFields = evaluation.MissingContextFields,
            };
        }

        foreach (var (key, value) in GetFlagOverrides(context))
        {
            result[key] = new RawReflagFlag
            {
                Key = key,
                Value = value,
            };
        }

        return result;
    }

    private IReadOnlyDictionary<string, bool> GetFlagOverrides(ReflagContext context)
    {
        var overrides = _effectiveFlagOverrides(context);
        return overrides ?? EmptyBooleanDictionary.Instance;
    }

    private bool TryGetOverride(ReflagContext context, string key, out bool value)
    {
        return GetFlagOverrides(context).TryGetValue(key, out value);
    }

    private async Task<FlagsCacheRefreshResult?> FetchDefinitionsAsync(int? waitForVersion)
    {
        var path = waitForVersion is null
            ? "features"
            : $"features?waitForVersion={Uri.EscapeDataString(waitForVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))}";

        var response = await GetAsync<GetFeaturesResponse>(path).ConfigureAwait(false);
        if (response is not null &&
            response.Features is not null &&
            response.FlagStateVersion is >= 0)
        {
            try
            {
                var compiledDefinitions = CompileDefinitions(response.Features);
                _canLoadFlagsFallbackProvider = false;
                _ = SaveFlagsFallbackDefinitionsAsync(compiledDefinitions);
                return new FlagsCacheRefreshResult(compiledDefinitions, response.FlagStateVersion);
            }
            catch (Exception error)
            {
                _logger.LogDebug(error, "failed to compile flag definitions from '{Path}'", path);
            }
        }

        var fallbackDefinitions = await LoadFlagsFallbackDefinitionsAsync().ConfigureAwait(false);
        return fallbackDefinitions is null ? null : new FlagsCacheRefreshResult(fallbackDefinitions, null);
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

    private async Task<CompiledFlagDefinition[]?> LoadFlagsFallbackDefinitionsAsync()
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

            if (snapshot.SchemaVersion != 1 || snapshot.Flags is null)
            {
                _logger.LogWarning("flagsFallbackProvider: invalid snapshot returned");
                return null;
            }

            var age = FormatFallbackAge(snapshot.SavedAt);
            if (age is null)
            {
                _logger.LogWarning("remote flags unavailable, using fallback flags ({SavedAt:O})", snapshot.SavedAt);
            }
            else
            {
                _logger.LogWarning("remote flags unavailable, using fallback flags fetched {Age} ago ({SavedAt:O})", age, snapshot.SavedAt);
            }

            return CompileDefinitions(snapshot.Flags);
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

    private async Task SaveFlagsFallbackDefinitionsAsync(IReadOnlyList<CompiledFlagDefinition> definitions)
    {
        if (_config.FlagsFallbackProvider is null)
        {
            return;
        }

        try
        {
            await _config.FlagsFallbackProvider.SaveAsync(
                _flagsFallbackProviderContext,
                new FlagsFallbackSnapshot
                {
                    SchemaVersion = 1,
                    SavedAt = DateTimeOffset.UtcNow,
                    Flags = definitions.Select(definition => definition.Definition).ToArray(),
                },
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

    private static CompiledFlagDefinition[] CompileDefinitions(IReadOnlyList<FlagDefinition> definitions)
    {
        return definitions.Select(definition =>
        {
            ValidateDefinition(definition);
            var evaluationRules = definition.Targeting.Rules
                .Select(rule => new EvaluationRule<bool>(FlagEvaluation.CompileFilter(rule.Filter), rule.Value))
                .ToArray();

            return new CompiledFlagDefinition
            {
                Definition = definition,
                Evaluator = FlagEvaluation.NewEvaluator(evaluationRules),
            };
        }).ToArray();
    }

    private static void ValidateDefinition(FlagDefinition definition)
    {
        ThrowHelpers.ThrowIfNull(definition, nameof(definition));
        ThrowHelpers.ThrowIfNullOrWhitespace(definition.Key, nameof(definition.Key));
        ThrowHelpers.ThrowIfNull(definition.Targeting, nameof(definition.Targeting));
        if (definition.Targeting.Version < 0)
        {
            throw new ArgumentException("flag definition targeting.version must be greater than or equal to zero.", nameof(definition));
        }

        ThrowHelpers.ThrowIfNull(definition.Targeting.Rules, nameof(definition.Targeting.Rules));
        foreach (var rule in definition.Targeting.Rules)
        {
            ThrowHelpers.ThrowIfNull(rule, nameof(definition.Targeting.Rules));
            ThrowHelpers.ThrowIfNull(rule.Filter, nameof(rule.Filter));
        }
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

    private void WarnMissingFlagContextFields(ReflagContext context, RawReflagFlag flag)
    {
        if (flag.MissingContextFields is not { Count: > 0 })
        {
            return;
        }

        var evaluationContext = ReflagContextNormalizer.ToEvaluationObject(context);
        var warningKey = HashObjectSerializer.HashObject(new Dictionary<string, object?>
        {
            ["type"] = "missing-context-warning",
            ["flagKey"] = flag.Key,
            ["missingContextFields"] = flag.MissingContextFields.ToArray(),
            ["evalContext"] = evaluationContext,
        });

        if (!_rateLimiter.IsAllowed(warningKey))
        {
            return;
        }

        _logger.LogWarning(
            "flag targeting rules might not be correctly evaluated due to missing context fields. {MissingContextFields}",
            new Dictionary<string, IReadOnlyList<string>>
            {
                [flag.Key] = flag.MissingContextFields,
            });
    }

    private void TryQueueCheckEvent(
        ReflagContext context,
        ReflagTelemetryOptions? telemetryOptions,
        RawReflagFlag flag)
    {
        if (telemetryOptions?.EnableTelemetry == false || _config.Offline)
        {
            return;
        }

        _ = SendFlagCheckEventSafeAsync(context, flag);
    }

    private async Task SendFlagCheckEventSafeAsync(ReflagContext context, RawReflagFlag flag)
    {
        try
        {
            await SendFlagCheckEventAsync(context, flag, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_disposed)
        {
            // no-op during disposal
        }
        catch (Exception error)
        {
            _logger.LogError(error, "failed to send check event for \"{FlagKey}\"", flag.Key);
        }
    }

    private async Task SendFlagCheckEventAsync(
        ReflagContext context,
        RawReflagFlag flag,
        CancellationToken cancellationToken)
    {
        var evaluationContext = ReflagContextNormalizer.ToEvaluationObject(context);
        var dedupeKey = HashObjectSerializer.HashObject(new Dictionary<string, object?>
        {
            ["type"] = "feature-flag-event",
            ["action"] = "check",
            ["key"] = flag.Key,
            ["targetingVersion"] = flag.TargetingVersion,
            ["evalResult"] = flag.Value,
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
                Key = flag.Key,
                TargetingVersion = flag.TargetingVersion,
                EvalResult = flag.Value,
                EvalContext = evaluationContext,
                EvalRuleResults = flag.RuleEvaluationResults,
                EvalMissingFields = flag.MissingContextFields,
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static string BuildContextKey(IReadOnlyDictionary<string, object?> context)
    {
        var flattenedContext = FlagEvaluation.FlattenJson(context);
        if (flattenedContext.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
            "&",
            flattenedContext
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    }

    private void SyncFlagOverridesNoLock()
    {
        var layers = _flagOverrideLayers.Select(layer => layer.Factory).ToArray();
        _effectiveFlagOverrides = context =>
        {
            var merged = CollectionHelpers.ToDictionary(_baseFlagOverrides(context), StringComparer.Ordinal);
            foreach (var layer in layers)
            {
                foreach (var (key, value) in layer(context) ?? EmptyBooleanDictionary.Instance)
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

    private static Func<ReflagContext, IReadOnlyDictionary<string, bool>> BuildBaseFlagOverrides(
        IReadOnlyDictionary<string, bool> envOverrides,
        IReadOnlyDictionary<string, bool>? optionOverrides,
        Func<ReflagContext, IReadOnlyDictionary<string, bool>>? optionOverridesFactory)
    {
        if (optionOverridesFactory is not null)
        {
            var normalizedFactory = NormalizeFlagOverrides(optionOverridesFactory);
            return context => MergeOverrides(envOverrides, normalizedFactory(context));
        }

        if (optionOverrides is not null)
        {
            var normalizedOverrides = CollectionHelpers.ToDictionary(optionOverrides, StringComparer.Ordinal);
            return _ => MergeOverrides(envOverrides, normalizedOverrides);
        }

        if (envOverrides.Count == 0)
        {
            return static _ => EmptyBooleanDictionary.Instance;
        }

        return _ => envOverrides;
    }

    private static Func<ReflagContext, IReadOnlyDictionary<string, bool>> NormalizeFlagOverrides(
        Func<ReflagContext, IReadOnlyDictionary<string, bool>> factory)
    {
        return context => factory(context) ?? EmptyBooleanDictionary.Instance;
    }

    private static IReadOnlyDictionary<string, bool> MergeOverrides(
        IReadOnlyDictionary<string, bool> lowerPriority,
        IReadOnlyDictionary<string, bool> higherPriority)
    {
        if (lowerPriority.Count == 0)
        {
            return CollectionHelpers.ToDictionary(higherPriority, StringComparer.Ordinal);
        }

        if (higherPriority.Count == 0)
        {
            return CollectionHelpers.ToDictionary(lowerPriority, StringComparer.Ordinal);
        }

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

        [JsonPropertyName("targetingVersion")]
        public int? TargetingVersion { get; init; }

        [JsonPropertyName("evalResult")]
        public bool EvalResult { get; init; }

        [JsonPropertyName("evalContext")]
        public IReadOnlyDictionary<string, object?>? EvalContext { get; init; }

        [JsonPropertyName("evalRuleResults")]
        public IReadOnlyList<bool>? EvalRuleResults { get; init; }

        [JsonPropertyName("evalMissingFields")]
        public IReadOnlyList<string>? EvalMissingFields { get; init; }
    }

    private readonly record struct FlagOverrideLayer(int Id, Func<ReflagContext, IReadOnlyDictionary<string, bool>> Factory);

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

    internal sealed class GetFeaturesResponse : ISuccessResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; init; }

        [JsonPropertyName("flagStateVersion")]
        public int? FlagStateVersion { get; init; }

        [JsonPropertyName("features")]
        public List<FlagDefinition>? Features { get; init; }
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

    private sealed class EmptyBooleanDictionary : IReadOnlyDictionary<string, bool>
    {
        public static readonly EmptyBooleanDictionary Instance = new();

        public int Count => 0;

        public IEnumerable<string> Keys => Array.Empty<string>();

        public IEnumerable<bool> Values => Array.Empty<bool>();

        public bool this[string key] => throw new KeyNotFoundException();

        public bool ContainsKey(string key) => false;

        public IEnumerator<KeyValuePair<string, bool>> GetEnumerator()
        {
            return Enumerable.Empty<KeyValuePair<string, bool>>().GetEnumerator();
        }

        public bool TryGetValue(string key, out bool value)
        {
            value = default;
            return false;
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
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
