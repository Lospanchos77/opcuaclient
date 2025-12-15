using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpcUaTrayClient.Core.Channel;
using OpcUaTrayClient.Core.Models;
using OpcUaTrayClient.Persistence.JsonFallback;
using OpcUaTrayClient.Persistence.MongoDB;

namespace OpcUaTrayClient.Persistence;

/// <summary>
/// Coordinates data persistence with automatic failover.
///
/// CRITICAL DESIGN:
/// 1. Consumes from DataPointChannel in background
/// 2. Batches data points (by count or time)
/// 3. Writes to active sink (MongoDB primary, JSON fallback)
/// 4. Automatically switches sinks based on health
/// 5. Triggers recovery when MongoDB becomes available
///
/// The OPC UA client only interacts with DataPointChannel.
/// This service handles all persistence complexity transparently.
/// </summary>
public sealed class DataPersistenceService : BackgroundService
{
    private readonly ILogger<DataPersistenceService> _logger;
    private readonly DataPointChannel _channel;
    private readonly MongoDataPointSink _mongoSink;
    private readonly JsonFileDataPointSink _jsonSink;
    private readonly MongoHealthMonitor _healthMonitor;
    private readonly RecoveryService _recoveryService;

    private readonly int _batchSize;
    private readonly TimeSpan _batchTimeout;
    private readonly bool _forceJsonOnly;
    private readonly bool _dryRunMode;

    private IDataPointSink _activeSink;
    private PersistenceMode _currentMode;
    private long _totalPersisted;

    /// <summary>
    /// Current persistence mode.
    /// </summary>
    public PersistenceMode CurrentMode => _currentMode;

    /// <summary>
    /// Name of the currently active sink.
    /// </summary>
    public string ActiveSinkName => _activeSink?.Name ?? "None";

    /// <summary>
    /// Total data points persisted.
    /// </summary>
    public long TotalPersisted => _totalPersisted;

    /// <summary>
    /// Event raised when persistence mode changes.
    /// </summary>
    public event EventHandler<PersistenceModeChangedEventArgs>? ModeChanged;

    public DataPersistenceService(
        DataPointChannel channel,
        MongoDataPointSink mongoSink,
        JsonFileDataPointSink jsonSink,
        MongoHealthMonitor healthMonitor,
        RecoveryService recoveryService,
        ILogger<DataPersistenceService> logger,
        int batchSize = 100,
        int batchTimeoutMs = 1000,
        bool forceJsonOnly = false,
        bool dryRunMode = false)
    {
        _channel = channel;
        _mongoSink = mongoSink;
        _jsonSink = jsonSink;
        _healthMonitor = healthMonitor;
        _recoveryService = recoveryService;
        _logger = logger;
        _batchSize = batchSize;
        _batchTimeout = TimeSpan.FromMilliseconds(batchTimeoutMs);
        _forceJsonOnly = forceJsonOnly;
        _dryRunMode = dryRunMode;

        // Initialize mode based on configuration
        if (_dryRunMode)
        {
            _currentMode = PersistenceMode.DryRun;
            _activeSink = _jsonSink; // Won't be used but must be set
        }
        else if (_forceJsonOnly)
        {
            _currentMode = PersistenceMode.JsonFallback;
            _activeSink = _jsonSink;
        }
        else
        {
            _currentMode = PersistenceMode.MongoDB;
            _activeSink = _mongoSink;
        }

        // Subscribe to health changes
        _healthMonitor.HealthChanged += OnHealthChanged;
    }

    private void OnHealthChanged(object? sender, StorageHealth health)
    {
        if (_dryRunMode || _forceJsonOnly) return;

        var previousMode = _currentMode;

        if (health == StorageHealth.Unhealthy && _currentMode == PersistenceMode.MongoDB)
        {
            // Switch to JSON fallback
            _currentMode = PersistenceMode.JsonFallback;
            _activeSink = _jsonSink;
            _logger.LogWarning("Switched to JSON fallback: MongoDB unhealthy");
            ModeChanged?.Invoke(this, new PersistenceModeChangedEventArgs(previousMode, _currentMode));
        }
        else if (health == StorageHealth.Healthy && _currentMode == PersistenceMode.JsonFallback)
        {
            // Switch back to MongoDB
            _currentMode = PersistenceMode.MongoDB;
            _activeSink = _mongoSink;
            _logger.LogInformation("Switched back to MongoDB: connection restored");
            ModeChanged?.Invoke(this, new PersistenceModeChangedEventArgs(previousMode, _currentMode));

            // Trigger recovery of pending JSON files
            _ = _recoveryService.StartRecoveryAsync();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DataPersistenceService started. Mode: {Mode}, BatchSize: {BatchSize}, Timeout: {Timeout}ms",
            _currentMode, _batchSize, _batchTimeout.TotalMilliseconds);

        // Start health monitoring
        _healthMonitor.Start();

        var batch = new List<OpcUaDataPoint>(_batchSize);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                batch.Clear();
                var batchStartTime = DateTime.UtcNow;

                // Collect batch up to size or timeout
                while (batch.Count < _batchSize)
                {
                    var remainingTime = _batchTimeout - (DateTime.UtcNow - batchStartTime);
                    if (remainingTime <= TimeSpan.Zero) break;

                    // Try to read immediately available items
                    while (batch.Count < _batchSize && _channel.Reader.TryRead(out var item))
                    {
                        batch.Add(item);
                    }

                    if (batch.Count >= _batchSize) break;

                    // Wait for more data or timeout
                    if (batch.Count == 0)
                    {
                        // No items yet - wait indefinitely for first item
                        try
                        {
                            await _channel.Reader.WaitToReadAsync(stoppingToken);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                    else
                    {
                        // Have some items - wait with timeout
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        cts.CancelAfter(remainingTime);

                        try
                        {
                            await _channel.Reader.WaitToReadAsync(cts.Token);
                        }
                        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                        {
                            // Timeout reached, flush current batch
                            break;
                        }
                    }
                }

                // Persist the batch
                if (batch.Count > 0)
                {
                    await PersistBatchAsync(batch, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
        finally
        {
            // Drain remaining items on shutdown
            _logger.LogInformation("DataPersistenceService stopping. Draining remaining data points...");

            batch.Clear();
            while (_channel.Reader.TryRead(out var item))
            {
                batch.Add(item);
                if (batch.Count >= _batchSize)
                {
                    await PersistBatchAsync(batch, CancellationToken.None);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                await PersistBatchAsync(batch, CancellationToken.None);
            }

            await _healthMonitor.StopAsync();
            _logger.LogInformation("DataPersistenceService stopped. Total persisted: {Total}", _totalPersisted);
        }
    }

    private async Task PersistBatchAsync(IReadOnlyList<OpcUaDataPoint> batch, CancellationToken ct)
    {
        if (_dryRunMode)
        {
            // Dry-run: just count, don't persist
            Interlocked.Add(ref _totalPersisted, batch.Count);
            _logger.LogDebug("Dry-run: Skipped {Count} data points", batch.Count);
            return;
        }

        var currentSink = _activeSink;

        try
        {
            var success = await currentSink.WriteAsync(batch, ct);

            if (success)
            {
                Interlocked.Add(ref _totalPersisted, batch.Count);
            }
            else if (currentSink == _mongoSink && !_forceJsonOnly)
            {
                // MongoDB write failed - immediate fallback for this batch
                _logger.LogWarning("MongoDB write failed, falling back to JSON for this batch");

                success = await _jsonSink.WriteAsync(batch, ct);
                if (success)
                {
                    Interlocked.Add(ref _totalPersisted, batch.Count);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Persistence failed for {Count} data points", batch.Count);

            // Last resort - try JSON fallback
            if (currentSink != _jsonSink && !_forceJsonOnly)
            {
                try
                {
                    var success = await _jsonSink.WriteAsync(batch, ct);
                    if (success)
                    {
                        Interlocked.Add(ref _totalPersisted, batch.Count);
                        _logger.LogWarning("Recovered batch via JSON fallback");
                    }
                }
                catch (Exception jsonEx)
                {
                    _logger.LogCritical(jsonEx, "CRITICAL: Both MongoDB and JSON persistence failed! Data loss: {Count} points",
                        batch.Count);
                }
            }
        }
    }

    /// <summary>
    /// Forces a switch to a specific persistence mode.
    /// </summary>
    public void ForceMode(PersistenceMode mode)
    {
        var previousMode = _currentMode;
        _currentMode = mode;

        _activeSink = mode switch
        {
            PersistenceMode.MongoDB => _mongoSink,
            PersistenceMode.JsonFallback => _jsonSink,
            PersistenceMode.DryRun => _jsonSink, // Won't be used
            _ => _jsonSink
        };

        _logger.LogInformation("Persistence mode forced: {Previous} -> {New}", previousMode, mode);
        ModeChanged?.Invoke(this, new PersistenceModeChangedEventArgs(previousMode, mode));
    }

    public override void Dispose()
    {
        _healthMonitor.HealthChanged -= OnHealthChanged;
        base.Dispose();
    }
}

/// <summary>
/// Event args for persistence mode changes.
/// </summary>
public sealed class PersistenceModeChangedEventArgs : EventArgs
{
    public PersistenceMode PreviousMode { get; }
    public PersistenceMode NewMode { get; }

    public PersistenceModeChangedEventArgs(PersistenceMode previousMode, PersistenceMode newMode)
    {
        PreviousMode = previousMode;
        NewMode = newMode;
    }
}
