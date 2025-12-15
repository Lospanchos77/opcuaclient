using Microsoft.Extensions.Logging;
using OpcUaTrayClient.Core.Models;
using OpcUaTrayClient.Persistence.MongoDB;

namespace OpcUaTrayClient.Persistence.JsonFallback;

/// <summary>
/// Recovers data from JSON fallback files to MongoDB.
///
/// Triggered when MongoDB becomes healthy after a failover period.
/// Processes files in chronological order to maintain data ordering.
/// Archives files after successful recovery (never deletes).
/// Stops if MongoDB becomes unavailable again.
/// </summary>
public sealed class RecoveryService
{
    private readonly ILogger<RecoveryService> _logger;
    private readonly JsonFileDataPointSink _jsonSink;
    private readonly MongoDataPointSink _mongoSink;
    private readonly MongoHealthMonitor _healthMonitor;
    private readonly int _batchSize;

    private bool _isRecovering;
    private CancellationTokenSource? _recoveryCts;
    private int _recoveredFileCount;
    private long _recoveredPointCount;

    /// <summary>
    /// Whether recovery is currently in progress.
    /// </summary>
    public bool IsRecovering => _isRecovering;

    /// <summary>
    /// Number of files recovered in current/last recovery session.
    /// </summary>
    public int RecoveredFileCount => _recoveredFileCount;

    /// <summary>
    /// Number of data points recovered in current/last recovery session.
    /// </summary>
    public long RecoveredPointCount => _recoveredPointCount;

    /// <summary>
    /// Event raised when recovery status changes.
    /// </summary>
    public event EventHandler<RecoveryStatusEventArgs>? StatusChanged;

    public RecoveryService(
        JsonFileDataPointSink jsonSink,
        MongoDataPointSink mongoSink,
        MongoHealthMonitor healthMonitor,
        ILogger<RecoveryService> logger,
        int batchSize = 100)
    {
        _jsonSink = jsonSink;
        _mongoSink = mongoSink;
        _healthMonitor = healthMonitor;
        _logger = logger;
        _batchSize = batchSize;
    }

    /// <summary>
    /// Starts the recovery process asynchronously.
    /// Safe to call multiple times - will only run one recovery at a time.
    /// </summary>
    public async Task StartRecoveryAsync()
    {
        if (_isRecovering)
        {
            _logger.LogDebug("Recovery already in progress, skipping");
            return;
        }

        _isRecovering = true;
        _recoveryCts = new CancellationTokenSource();
        _recoveredFileCount = 0;
        _recoveredPointCount = 0;

        StatusChanged?.Invoke(this, new RecoveryStatusEventArgs(RecoveryStatus.Started, 0, 0));

        try
        {
            await RecoverFilesAsync(_recoveryCts.Token);
            StatusChanged?.Invoke(this, new RecoveryStatusEventArgs(
                RecoveryStatus.Completed, _recoveredFileCount, _recoveredPointCount));
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Recovery cancelled");
            StatusChanged?.Invoke(this, new RecoveryStatusEventArgs(
                RecoveryStatus.Cancelled, _recoveredFileCount, _recoveredPointCount));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recovery failed");
            StatusChanged?.Invoke(this, new RecoveryStatusEventArgs(
                RecoveryStatus.Failed, _recoveredFileCount, _recoveredPointCount));
        }
        finally
        {
            _isRecovering = false;
            _recoveryCts?.Dispose();
            _recoveryCts = null;
        }
    }

    /// <summary>
    /// Stops the current recovery process.
    /// </summary>
    public void StopRecovery()
    {
        _recoveryCts?.Cancel();
    }

    private async Task RecoverFilesAsync(CancellationToken ct)
    {
        var pendingFiles = _jsonSink.GetPendingFiles().ToList();

        if (pendingFiles.Count == 0)
        {
            _logger.LogInformation("No pending JSON files to recover");
            return;
        }

        _logger.LogInformation("Starting recovery of {Count} JSON files", pendingFiles.Count);

        foreach (var filePath in pendingFiles)
        {
            ct.ThrowIfCancellationRequested();

            // Check MongoDB health before processing each file
            if (_healthMonitor.CurrentHealth == StorageHealth.Unhealthy)
            {
                _logger.LogWarning("MongoDB became unhealthy during recovery. Stopping.");
                break;
            }

            try
            {
                await RecoverFileAsync(filePath, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to recover file {File}", filePath);
                // Continue with next file instead of stopping
            }
        }

        _logger.LogInformation("Recovery completed. Files: {Files}, Points: {Points}",
            _recoveredFileCount, _recoveredPointCount);
    }

    private async Task RecoverFileAsync(string filePath, CancellationToken ct)
    {
        var filename = Path.GetFileName(filePath);
        _logger.LogDebug("Recovering file: {File}", filename);

        // Read data points from file
        var dataPoints = await _jsonSink.ReadFileAsync(filePath, ct);
        if (dataPoints.Count == 0)
        {
            _logger.LogWarning("File {File} is empty or invalid, archiving", filename);
            _jsonSink.ArchiveFile(filePath);
            return;
        }

        // Insert in batches to MongoDB
        var totalInserted = 0;
        for (int i = 0; i < dataPoints.Count; i += _batchSize)
        {
            ct.ThrowIfCancellationRequested();

            // Check health before each batch
            if (_healthMonitor.CurrentHealth == StorageHealth.Unhealthy)
            {
                _logger.LogWarning("MongoDB became unhealthy during file recovery. File partially recovered: {File}", filename);
                return; // Don't archive, will retry later
            }

            var batch = dataPoints.Skip(i).Take(_batchSize).ToList();
            var success = await _mongoSink.WriteAsync(batch, ct);

            if (!success)
            {
                _logger.LogWarning("Failed to insert batch to MongoDB. File partially recovered: {File}", filename);
                return; // Don't archive, will retry later
            }

            totalInserted += batch.Count;
        }

        // All batches inserted successfully - archive the file
        _jsonSink.ArchiveFile(filePath);
        _recoveredFileCount++;
        _recoveredPointCount += totalInserted;

        _logger.LogInformation("Recovered file {File}: {Count} data points", filename, totalInserted);
        StatusChanged?.Invoke(this, new RecoveryStatusEventArgs(
            RecoveryStatus.InProgress, _recoveredFileCount, _recoveredPointCount));
    }
}

/// <summary>
/// Recovery status.
/// </summary>
public enum RecoveryStatus
{
    Started,
    InProgress,
    Completed,
    Cancelled,
    Failed
}

/// <summary>
/// Event args for recovery status changes.
/// </summary>
public sealed class RecoveryStatusEventArgs : EventArgs
{
    public RecoveryStatus Status { get; }
    public int FilesRecovered { get; }
    public long PointsRecovered { get; }

    public RecoveryStatusEventArgs(RecoveryStatus status, int filesRecovered, long pointsRecovered)
    {
        Status = status;
        FilesRecovered = filesRecovered;
        PointsRecovered = pointsRecovered;
    }
}
