using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using OpcUaTrayClient.Core.Models;

namespace OpcUaTrayClient.Persistence.MongoDB;

/// <summary>
/// Monitors MongoDB health with a dedicated connection.
///
/// KEY DESIGN:
/// - Uses a SEPARATE MongoClient with aggressive timeouts
/// - Health checks run in their own task, never blocking data writes
/// - Triggers failover to JSON when MongoDB becomes unhealthy
///
/// This ensures OPC UA acquisition continues regardless of MongoDB health.
/// </summary>
public sealed class MongoHealthMonitor : IDisposable
{
    private readonly ILogger<MongoHealthMonitor> _logger;
    private readonly IMongoClient _healthCheckClient;
    private readonly TimeSpan _checkInterval;
    private readonly TimeSpan _pingTimeout;
    private readonly int _failureThreshold;

    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private StorageHealth _currentHealth = StorageHealth.Unknown;
    private int _consecutiveFailures;
    private bool _disposed;

    /// <summary>
    /// Current health status.
    /// </summary>
    public StorageHealth CurrentHealth => _currentHealth;

    /// <summary>
    /// Event raised when health status changes.
    /// </summary>
    public event EventHandler<StorageHealth>? HealthChanged;

    public MongoHealthMonitor(
        string connectionString,
        ILogger<MongoHealthMonitor> logger,
        int checkIntervalSeconds = 5,
        int pingTimeoutSeconds = 2,
        int failureThreshold = 3)
    {
        _logger = logger;
        _checkInterval = TimeSpan.FromSeconds(checkIntervalSeconds);
        _pingTimeout = TimeSpan.FromSeconds(pingTimeoutSeconds);
        _failureThreshold = failureThreshold;

        // Dedicated client with aggressive timeouts for health checks
        // This client is separate from the data write client
        var settings = MongoClientSettings.FromConnectionString(connectionString);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(pingTimeoutSeconds);
        settings.ConnectTimeout = TimeSpan.FromSeconds(pingTimeoutSeconds);
        settings.SocketTimeout = TimeSpan.FromSeconds(pingTimeoutSeconds);
        settings.MaxConnectionPoolSize = 2; // Minimal pool for health checks
        settings.MinConnectionPoolSize = 0;
        settings.WaitQueueTimeout = TimeSpan.FromSeconds(1);

        _healthCheckClient = new MongoClient(settings);
        _logger.LogInformation("MongoHealthMonitor created. Check interval: {Interval}s, Timeout: {Timeout}s",
            checkIntervalSeconds, pingTimeoutSeconds);
    }

    /// <summary>
    /// Starts the background health monitoring task.
    /// </summary>
    public void Start()
    {
        if (_monitorTask != null)
        {
            _logger.LogWarning("Health monitor already running");
            return;
        }

        _monitorCts = new CancellationTokenSource();
        _monitorTask = MonitorLoopAsync(_monitorCts.Token);
        _logger.LogInformation("MongoDB health monitoring started");
    }

    /// <summary>
    /// Stops the health monitoring task.
    /// </summary>
    public async Task StopAsync()
    {
        if (_monitorCts == null) return;

        _monitorCts.Cancel();

        if (_monitorTask != null)
        {
            try
            {
                await _monitorTask;
            }
            catch (OperationCanceledException) { }
        }

        _monitorCts.Dispose();
        _monitorCts = null;
        _monitorTask = null;
        _logger.LogInformation("MongoDB health monitoring stopped");
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        // Initial health check
        await PerformHealthCheckAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_checkInterval, ct);
                await PerformHealthCheckAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in health monitor loop");
            }
        }
    }

    private async Task PerformHealthCheckAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        StorageHealth newHealth;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_pingTimeout);

            // Lightweight ping command
            var result = await _healthCheckClient
                .GetDatabase("admin")
                .RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cts.Token);

            sw.Stop();
            _consecutiveFailures = 0;

            // Check latency for degraded state
            if (sw.ElapsedMilliseconds > 500)
            {
                _logger.LogWarning("MongoDB ping latency high: {Ms}ms", sw.ElapsedMilliseconds);
                newHealth = StorageHealth.Degraded;
            }
            else
            {
                newHealth = StorageHealth.Healthy;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Ping timeout
            _consecutiveFailures++;
            _logger.LogWarning("MongoDB health check timed out ({Timeout}ms). Consecutive failures: {Count}",
                _pingTimeout.TotalMilliseconds, _consecutiveFailures);
            newHealth = _consecutiveFailures >= _failureThreshold
                ? StorageHealth.Unhealthy
                : StorageHealth.Degraded;
        }
        catch (MongoException ex)
        {
            _consecutiveFailures++;
            _logger.LogWarning(ex, "MongoDB health check failed. Consecutive failures: {Count}", _consecutiveFailures);
            newHealth = _consecutiveFailures >= _failureThreshold
                ? StorageHealth.Unhealthy
                : StorageHealth.Degraded;
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            _logger.LogError(ex, "Unexpected error during MongoDB health check. Consecutive failures: {Count}",
                _consecutiveFailures);
            newHealth = _consecutiveFailures >= _failureThreshold
                ? StorageHealth.Unhealthy
                : StorageHealth.Degraded;
        }

        // Update and notify if changed
        if (newHealth != _currentHealth)
        {
            var oldHealth = _currentHealth;
            _currentHealth = newHealth;
            _logger.LogInformation("MongoDB health changed: {Old} -> {New}", oldHealth, newHealth);
            HealthChanged?.Invoke(this, newHealth);
        }
    }

    /// <summary>
    /// Performs an immediate health check without waiting for the next interval.
    /// </summary>
    public async Task<StorageHealth> CheckNowAsync(CancellationToken ct = default)
    {
        await PerformHealthCheckAsync(ct);
        return _currentHealth;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _monitorCts?.Cancel();
        _monitorCts?.Dispose();

        // Note: MongoClient doesn't implement IDisposable
        // The connection pool will be cleaned up by the runtime
    }
}
