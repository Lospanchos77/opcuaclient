using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpcUaTrayClient.Core.Channel;
using OpcUaTrayClient.Core.Configuration;
using OpcUaTrayClient.Core.Models;
using OpcUaTrayClient.OpcUa;
using OpcUaTrayClient.Persistence;
using OpcUaTrayClient.Persistence.MongoDB;

namespace OpcUaTrayClient.Service;

/// <summary>
/// Windows Service worker that manages OPC UA acquisition.
/// Handles the same lifecycle as TrayApplicationContext but without UI.
/// </summary>
public class OpcUaWorkerService : BackgroundService
{
    private readonly ILogger<OpcUaWorkerService> _logger;
    private readonly ConfigurationService _configService;
    private readonly OpcUaClientManager _opcUaManager;
    private readonly DataPersistenceService _persistenceService;
    private readonly DataPointChannel _channel;
    private readonly MongoHealthMonitor _healthMonitor;

    private Task? _persistenceTask;
    private CancellationTokenSource? _acquisitionCts;
    private DateTime _lastLogTime = DateTime.MinValue;

    public OpcUaWorkerService(
        ILogger<OpcUaWorkerService> logger,
        ConfigurationService configService,
        OpcUaClientManager opcUaManager,
        DataPersistenceService persistenceService,
        DataPointChannel channel,
        MongoHealthMonitor healthMonitor)
    {
        _logger = logger;
        _configService = configService;
        _opcUaManager = opcUaManager;
        _persistenceService = persistenceService;
        _channel = channel;
        _healthMonitor = healthMonitor;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OPC UA Service starting...");

        try
        {
            // Load configuration
            await _configService.LoadAsync(stoppingToken);
            _logger.LogInformation("Configuration loaded: {ServerCount} servers configured",
                _configService.Current.Servers.Count);

            // Subscribe to events for logging
            _opcUaManager.ServerConnectionStateChanged += OnServerConnectionStateChanged;
            _persistenceService.ModeChanged += OnPersistenceModeChanged;
            _healthMonitor.HealthChanged += OnHealthChanged;

            // Start acquisition
            await StartAcquisitionAsync(stoppingToken);

            // Keep running and log status periodically
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                LogStatus();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("OPC UA Service shutdown requested");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in OPC UA Service");
            throw;
        }
    }

    private async Task StartAcquisitionAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting acquisition...");

        // Start persistence service
        _acquisitionCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _persistenceTask = _persistenceService.StartAsync(_acquisitionCts.Token);

        // Connect to all configured OPC UA servers
        var enabledServers = _configService.Current.Servers.Where(s => s.Enabled).ToList();
        if (enabledServers.Count > 0)
        {
            await _opcUaManager.ConnectAllAsync(enabledServers, _acquisitionCts.Token);
            _logger.LogInformation("Acquisition started: {Connected}/{Total} servers connected",
                _opcUaManager.ConnectedServerCount, _opcUaManager.TotalServerCount);
        }
        else
        {
            _logger.LogWarning("No enabled servers configured - waiting for configuration");
        }
    }

    private async Task StopAcquisitionAsync()
    {
        _logger.LogInformation("Stopping acquisition...");

        try
        {
            // Disconnect from all OPC UA servers
            await _opcUaManager.DisconnectAllAsync();

            // Stop persistence service
            _acquisitionCts?.Cancel();
            if (_persistenceTask != null)
            {
                try { await _persistenceTask; }
                catch (OperationCanceledException) { }
            }

            // Complete channel to drain remaining items
            _channel.Complete();

            _logger.LogInformation("Acquisition stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping acquisition");
        }
    }

    private void LogStatus()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastLogTime).TotalMinutes < 5)
            return; // Log every 5 minutes max

        _lastLogTime = now;

        _logger.LogInformation(
            "Status: {Connected}/{Total} servers, {PointsReceived} points received, " +
            "{PointsPersisted} persisted, Queue: {QueueDepth}, Storage: {Storage} ({Health})",
            _opcUaManager.ConnectedServerCount,
            _opcUaManager.TotalServerCount,
            _opcUaManager.TotalDataPointsReceived,
            _persistenceService.TotalPersisted,
            _channel.CurrentCount,
            _persistenceService.ActiveSinkName,
            _healthMonitor.CurrentHealth);
    }

    private void OnServerConnectionStateChanged(object? sender, ServerConnectionStateChangedEventArgs e)
    {
        _logger.LogInformation("Server {ServerName} ({ServerId}): {OldState} -> {NewState}",
            e.ServerName, e.ServerId, e.OldState, e.NewState);
    }

    private void OnPersistenceModeChanged(object? sender, PersistenceModeChangedEventArgs e)
    {
        _logger.LogWarning("Persistence mode changed: {Previous} -> {New}", e.PreviousMode, e.NewMode);
    }

    private void OnHealthChanged(object? sender, StorageHealth health)
    {
        _logger.LogInformation("MongoDB health: {Health}", health);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("OPC UA Service stopping...");
        await StopAcquisitionAsync();
        await base.StopAsync(cancellationToken);
        _logger.LogInformation("OPC UA Service stopped");
    }

    public override void Dispose()
    {
        _acquisitionCts?.Dispose();
        base.Dispose();
    }
}
