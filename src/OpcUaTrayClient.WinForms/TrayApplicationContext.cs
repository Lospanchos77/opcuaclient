using System.Drawing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpcUaTrayClient.Core.Channel;
using OpcUaTrayClient.Core.Configuration;
using OpcUaTrayClient.Core.Models;
using OpcUaTrayClient.OpcUa;
using OpcUaTrayClient.Persistence;
using OpcUaTrayClient.Persistence.MongoDB;
using OpcUaTrayClient.WinForms.Forms;
using OpcUaTrayClient.WinForms.Services;

namespace OpcUaTrayClient.WinForms;

/// <summary>
/// Application context for system tray operation.
///
/// Manages:
/// - NotifyIcon with context menu
/// - Status updates via IProgress&lt;StatusUpdate&gt;
/// - Service lifecycle (standalone or Windows Service mode)
/// - ConfigForm instance
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly IServiceProvider _services;
    private readonly ILogger<TrayApplicationContext> _logger;
    private readonly ConfigurationService _configService;
    private readonly OpcUaClientManager _opcUaManager;  // V2.0.0: Multi-server manager
    private readonly DataPersistenceService _persistenceService;
    private readonly DataPointChannel _channel;
    private readonly MongoHealthMonitor _healthMonitor;

    // Windows Service mode support
    private readonly bool _useWindowsService;
    private readonly WindowsServiceHelper? _serviceHelper;

    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private readonly Progress<StatusUpdate> _statusProgress;

    private ToolStripMenuItem _statusMenuItem = null!;
    private ToolStripMenuItem _queueMenuItem = null!;
    private ToolStripMenuItem _storageMenuItem = null!;
    private ToolStripMenuItem _startMenuItem = null!;
    private ToolStripMenuItem _stopMenuItem = null!;
    private ToolStripMenuItem? _serviceStatusMenuItem;

    private ConfigForm? _configForm;
    private bool _acquisitionRunning;
    private CancellationTokenSource? _acquisitionCts;
    private Task? _persistenceTask;

    private long _lastDataPointCount;
    private DateTime _lastStatusUpdate = DateTime.UtcNow;

    // Cached icons to prevent memory leak (icons are reused instead of recreated)
    private readonly Dictionary<Color, Icon> _iconCache = new();
    private Icon? _currentIcon;

    public TrayApplicationContext(IServiceProvider services)
    {
        _services = services;
        _logger = services.GetRequiredService<ILogger<TrayApplicationContext>>();
        _configService = services.GetRequiredService<ConfigurationService>();
        _opcUaManager = services.GetRequiredService<OpcUaClientManager>();  // V2.0.0
        _persistenceService = services.GetRequiredService<DataPersistenceService>();
        _channel = services.GetRequiredService<DataPointChannel>();
        _healthMonitor = services.GetRequiredService<MongoHealthMonitor>();

        // Check if we should use Windows Service mode
        _useWindowsService = _configService.Current.UseWindowsService;
        if (_useWindowsService)
        {
            var serviceName = _configService.Current.WindowsServiceName;
            _serviceHelper = new WindowsServiceHelper(
                services.GetRequiredService<ILogger<WindowsServiceHelper>>(),
                serviceName);
            _logger.LogInformation("Running in Windows Service mode, controlling service: {ServiceName}", serviceName);
        }
        else
        {
            _logger.LogInformation("Running in standalone mode (local acquisition)");
        }

        // Create context menu
        _contextMenu = CreateContextMenu();

        // Create notify icon
        _notifyIcon = new NotifyIcon
        {
            Icon = CreateDefaultIcon(),
            Visible = true,
            Text = "Client OPC UA\nDéconnecté",
            ContextMenuStrip = _contextMenu
        };

        _notifyIcon.DoubleClick += (s, e) => ShowConfigForm();

        // Status update progress (captures UI SynchronizationContext)
        _statusProgress = new Progress<StatusUpdate>(OnStatusUpdate);

        // Status timer for periodic updates
        _statusTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _statusTimer.Tick += OnStatusTimerTick;
        _statusTimer.Start();

        // Subscribe to events based on mode
        if (!_useWindowsService)
        {
            // Standalone mode: subscribe to local service events
            _opcUaManager.ServerConnectionStateChanged += OnServerConnectionStateChanged;
            _persistenceService.ModeChanged += OnPersistenceModeChanged;
            _healthMonitor.HealthChanged += OnHealthChanged;
        }

        // Show config form on first launch (no servers configured yet)
        var hasSubscriptions = _configService.Current.Servers.Any(s => s.Subscriptions.Count > 0);
        if (_configService.Current.Servers.Count == 0 || !hasSubscriptions)
        {
            _logger.LogInformation("First launch detected, opening configuration form");
            ShowConfigForm();
        }
        // Auto-start acquisition if there are saved servers with subscriptions
        else if (!_useWindowsService)
        {
            // Standalone mode: auto-start local acquisition
            _logger.LogInformation("Servers found ({Count}), auto-starting acquisition", _configService.Current.Servers.Count);
            _ = StartAcquisitionAsync();
        }
        else
        {
            // Windows Service mode: check service status
            _logger.LogInformation("Windows Service mode - service status: {Status}", _serviceHelper?.StatusText ?? "Unknown");
        }

        _logger.LogInformation("TrayApplicationContext initialized (Mode: {Mode})",
            _useWindowsService ? "Windows Service" : "Standalone");
    }

    private ContextMenuStrip CreateContextMenu()
    {
        var menu = new ContextMenuStrip();

        // Status items (disabled, info only)
        _statusMenuItem = new ToolStripMenuItem("État : Déconnecté") { Enabled = false };
        _queueMenuItem = new ToolStripMenuItem("File : 0") { Enabled = false };
        _storageMenuItem = new ToolStripMenuItem("Stockage : Inconnu") { Enabled = false };

        menu.Items.Add(_statusMenuItem);
        menu.Items.Add(_queueMenuItem);
        menu.Items.Add(_storageMenuItem);

        // Windows Service mode: show service status
        if (_useWindowsService)
        {
            _serviceStatusMenuItem = new ToolStripMenuItem("Service : Vérification...") { Enabled = false };
            menu.Items.Add(_serviceStatusMenuItem);
        }

        menu.Items.Add(new ToolStripSeparator());

        // Open configuration
        var configItem = new ToolStripMenuItem("Ouvrir la configuration...", null, (s, e) => ShowConfigForm());
        menu.Items.Add(configItem);

        menu.Items.Add(new ToolStripSeparator());

        if (_useWindowsService)
        {
            // Windows Service mode: control the service
            _startMenuItem = new ToolStripMenuItem("Démarrer le service", null, async (s, e) => await StartServiceAsync());
            _stopMenuItem = new ToolStripMenuItem("Arrêter le service", null, async (s, e) => await StopServiceAsync());

            menu.Items.Add(_startMenuItem);
            menu.Items.Add(_stopMenuItem);

            // Restart service
            var restartItem = new ToolStripMenuItem("Redémarrer le service", null, async (s, e) =>
            {
                _logger.LogInformation("Restarting Windows Service...");
                _startMenuItem.Enabled = false;
                _stopMenuItem.Enabled = false;

                var success = await _serviceHelper!.RestartAsync();

                if (success)
                {
                    _notifyIcon.ShowBalloonTip(3000, "Client OPC UA", "Service redémarré avec succès", ToolTipIcon.Info);
                }
                else
                {
                    MessageBox.Show("Échec du redémarrage du service.\nVérifiez les droits administrateur.",
                        "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }

                UpdateServiceMenuState();
            });
            menu.Items.Add(restartItem);

            menu.Items.Add(new ToolStripSeparator());

            // View service logs
            var logsItem = new ToolStripMenuItem("Voir les logs du service...", null, (s, e) => ShowServiceLogs());
            menu.Items.Add(logsItem);
        }
        else
        {
            // Standalone mode: local acquisition controls
            _startMenuItem = new ToolStripMenuItem("Démarrer l'acquisition", null, async (s, e) => await StartAcquisitionAsync());
            _stopMenuItem = new ToolStripMenuItem("Arrêter l'acquisition", null, async (s, e) => await StopAcquisitionAsync());
            _stopMenuItem.Enabled = false;

            menu.Items.Add(_startMenuItem);
            menu.Items.Add(_stopMenuItem);

            // Restart connection
            var restartItem = new ToolStripMenuItem("Redémarrer la connexion OPC UA", null, async (s, e) =>
            {
                _logger.LogInformation("Restarting OPC UA connection...");

                // Force stop regardless of current state
                try
                {
                    await _opcUaManager.DisconnectAllAsync();
                    _acquisitionCts?.Cancel();
                    if (_persistenceTask != null)
                    {
                        try { await _persistenceTask; } catch (OperationCanceledException) { }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error during forced disconnect");
                }

                _acquisitionRunning = false;
                _startMenuItem.Enabled = true;
                _stopMenuItem.Enabled = false;

                // Reload configuration and restart
                await _configService.ReloadAsync();
                await StartAcquisitionAsync();

                _logger.LogInformation("OPC UA connection restart complete");
            });
            menu.Items.Add(restartItem);
        }

        menu.Items.Add(new ToolStripSeparator());

        // Mode indicator
        var modeItem = new ToolStripMenuItem($"Mode : {(_useWindowsService ? "Service Windows" : "Standalone")}") { Enabled = false };
        menu.Items.Add(modeItem);

        // Exit
        var exitItem = new ToolStripMenuItem("Quitter", null, OnExit);
        menu.Items.Add(exitItem);

        return menu;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // WINDOWS SERVICE MODE METHODS
    // ─────────────────────────────────────────────────────────────────────────

    private async Task StartServiceAsync()
    {
        if (_serviceHelper == null) return;

        _startMenuItem.Enabled = false;
        _logger.LogInformation("Starting Windows Service...");

        var success = await _serviceHelper.StartAsync();

        if (success)
        {
            _notifyIcon.ShowBalloonTip(3000, "Client OPC UA", "Service démarré avec succès", ToolTipIcon.Info);
        }
        else
        {
            MessageBox.Show("Échec du démarrage du service.\nVérifiez que le service est installé et que vous avez les droits administrateur.",
                "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        UpdateServiceMenuState();
    }

    private async Task StopServiceAsync()
    {
        if (_serviceHelper == null) return;

        _stopMenuItem.Enabled = false;
        _logger.LogInformation("Stopping Windows Service...");

        var success = await _serviceHelper.StopAsync();

        if (success)
        {
            _notifyIcon.ShowBalloonTip(3000, "Client OPC UA", "Service arrêté", ToolTipIcon.Info);
        }
        else
        {
            MessageBox.Show("Échec de l'arrêt du service.\nVérifiez les droits administrateur.",
                "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        UpdateServiceMenuState();
    }

    private void UpdateServiceMenuState()
    {
        if (_serviceHelper == null) return;

        var isRunning = _serviceHelper.IsRunning;
        var isStopped = _serviceHelper.IsStopped;

        _startMenuItem.Enabled = isStopped || !_serviceHelper.IsInstalled;
        _stopMenuItem.Enabled = isRunning;

        if (_serviceStatusMenuItem != null)
        {
            _serviceStatusMenuItem.Text = $"Service : {_serviceHelper.StatusText}";
        }
    }

    private void ShowServiceLogs()
    {
        if (_serviceHelper == null) return;

        var logPath = _serviceHelper.GetLogFilePath();

        // Try to open with default text editor
        try
        {
            if (File.Exists(logPath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = logPath,
                    UseShellExecute = true
                });
            }
            else
            {
                MessageBox.Show($"Fichier de log non trouvé:\n{logPath}", "Information",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open service log file");
            MessageBox.Show($"Impossible d'ouvrir le fichier de log:\n{ex.Message}", "Erreur",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // STANDALONE MODE METHODS
    // ─────────────────────────────────────────────────────────────────────────

    private void ShowConfigForm()
    {
        if (_configForm == null || _configForm.IsDisposed)
        {
            _configForm = new ConfigForm(_services);
            _configForm.FormClosed += (s, e) => _configForm = null;
        }

        _configForm.Show();
        _configForm.BringToFront();
        _configForm.Activate();
    }

    private async Task StartAcquisitionAsync()
    {
        if (_acquisitionRunning) return;

        try
        {
            _startMenuItem.Enabled = false;
            _logger.LogInformation("Starting acquisition...");

            // Start persistence service
            _acquisitionCts = new CancellationTokenSource();
            _persistenceTask = _persistenceService.StartAsync(_acquisitionCts.Token);

            // V2.0.0: Connect to all configured OPC UA servers
            var enabledServers = _configService.Current.Servers.Where(s => s.Enabled).ToList();
            if (enabledServers.Count > 0)
            {
                await _opcUaManager.ConnectAllAsync(enabledServers, _acquisitionCts.Token);
            }
            else
            {
                _logger.LogWarning("No enabled servers to connect to");
            }

            _acquisitionRunning = true;
            _startMenuItem.Enabled = false;
            _stopMenuItem.Enabled = true;

            _logger.LogInformation("Acquisition started ({Connected}/{Total} servers)",
                _opcUaManager.ConnectedServerCount, _opcUaManager.TotalServerCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start acquisition");
            _startMenuItem.Enabled = true;
            MessageBox.Show($"Échec du démarrage de l'acquisition :\n{ex.Message}", "Erreur",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task StopAcquisitionAsync()
    {
        if (!_acquisitionRunning) return;

        try
        {
            _stopMenuItem.Enabled = false;
            _logger.LogInformation("Stopping acquisition...");

            // V2.0.0: Disconnect from all OPC UA servers
            await _opcUaManager.DisconnectAllAsync();

            // Stop persistence service
            _acquisitionCts?.Cancel();
            if (_persistenceTask != null)
            {
                try { await _persistenceTask; } catch (OperationCanceledException) { }
            }

            _acquisitionRunning = false;
            _startMenuItem.Enabled = true;
            _stopMenuItem.Enabled = false;

            _logger.LogInformation("Acquisition stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping acquisition");
            _stopMenuItem.Enabled = true;
        }
    }

    private void OnStatusTimerTick(object? sender, EventArgs e)
    {
        if (_useWindowsService)
        {
            // Windows Service mode: update service status
            UpdateServiceMenuState();

            // Create a minimal status update for UI
            var serviceRunning = _serviceHelper?.IsRunning ?? false;
            var update = new StatusUpdate
            {
                ConnectionState = serviceRunning ? OpcUaConnectionState.Connected : OpcUaConnectionState.Disconnected,
                QueueDepth = 0,
                DroppedCount = 0,
                ActiveSink = serviceRunning ? "Service Windows" : "Service arrêté",
                MongoHealth = serviceRunning ? StorageHealth.Healthy : StorageHealth.Unknown,
                PersistenceMode = PersistenceMode.MongoDB,
                DataPointsPerSecond = 0,
                TotalDataPointsReceived = 0,
                TotalDataPointsPersisted = 0,
                LastError = null,
                LastDataPointTime = null,
                ConnectedServerCount = 0,
                TotalServerCount = _configService.Current.Servers.Count
            };

            ((IProgress<StatusUpdate>)_statusProgress).Report(update);
        }
        else
        {
            // Standalone mode: calculate points per second from local services
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastStatusUpdate).TotalSeconds;
            var currentCount = _opcUaManager.TotalDataPointsReceived;
            var pointsPerSecond = elapsed > 0 ? (int)((currentCount - _lastDataPointCount) / elapsed) : 0;

            _lastDataPointCount = currentCount;
            _lastStatusUpdate = now;

            var update = new StatusUpdate
            {
                QueueDepth = _channel.CurrentCount,
                DroppedCount = _channel.DroppedCount,
                ActiveSink = _persistenceService.ActiveSinkName,
                MongoHealth = _healthMonitor.CurrentHealth,
                PersistenceMode = _persistenceService.CurrentMode,
                DataPointsPerSecond = pointsPerSecond,
                TotalDataPointsReceived = currentCount,
                TotalDataPointsPersisted = _persistenceService.TotalPersisted,
                ConnectionState = _opcUaManager.GetAggregatedState(),  // V2.0.0: Aggregated state
                LastError = null,  // V2.0.0: Would need aggregation logic
                LastDataPointTime = _opcUaManager.LastDataPointTime,
                ConnectedServerCount = _opcUaManager.ConnectedServerCount,  // V2.0.0
                TotalServerCount = _opcUaManager.TotalServerCount  // V2.0.0
            };

            ((IProgress<StatusUpdate>)_statusProgress).Report(update);
        }
    }

    private void OnStatusUpdate(StatusUpdate update)
    {
        // Update menu items
        _statusMenuItem.Text = $"État : {update.ConnectionState}";
        _queueMenuItem.Text = $"File : {update.QueueDepth} (Perdus : {update.DroppedCount})";
        _storageMenuItem.Text = $"Stockage : {update.ActiveSink} ({update.MongoHealth})";

        // Update tooltip
        _notifyIcon.Text = $"Client OPC UA\n" +
            $"{update.ConnectionState}\n" +
            $"{update.DataPointsPerSecond} pts/sec\n" +
            $"File : {update.QueueDepth}";

        // Update icon based on health
        UpdateIcon(update);

        // Update config form if open
        _configForm?.UpdateStatus(update);
    }

    private void UpdateIcon(StatusUpdate update)
    {
        var color = update.MongoHealth switch
        {
            StorageHealth.Healthy => Color.Green,
            StorageHealth.Degraded => Color.Yellow,
            StorageHealth.Unhealthy => Color.Red,
            _ => Color.Gray
        };

        // Use cached icon to prevent memory leak
        var newIcon = GetOrCreateIcon(color);
        if (newIcon != _currentIcon)
        {
            _currentIcon = newIcon;
            _notifyIcon.Icon = newIcon;
        }
    }

    private Icon CreateDefaultIcon()
    {
        return GetOrCreateIcon(Color.Gray);
    }

    /// <summary>
    /// Gets an icon from cache or creates and caches it.
    /// Icons are cached to prevent memory leak from repeated icon creation.
    /// </summary>
    private Icon GetOrCreateIcon(Color color)
    {
        if (_iconCache.TryGetValue(color, out var cachedIcon))
        {
            return cachedIcon;
        }

        // Create a simple 16x16 icon with the specified color
        using var bitmap = new Bitmap(16, 16);
        using var graphics = Graphics.FromImage(bitmap);

        graphics.Clear(Color.Transparent);
        using var brush = new SolidBrush(color);
        graphics.FillEllipse(brush, 2, 2, 12, 12);
        using var pen = new Pen(Color.Black, 1);
        graphics.DrawEllipse(pen, 2, 2, 12, 12);

        var hIcon = bitmap.GetHicon();
        var icon = Icon.FromHandle(hIcon);

        // Clone the icon so we can safely store it (Icon.FromHandle doesn't own the handle)
        var clonedIcon = (Icon)icon.Clone();

        // Destroy the original handle to prevent leak
        DestroyIcon(hIcon);

        _iconCache[color] = clonedIcon;
        return clonedIcon;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);

    private void OnServerConnectionStateChanged(object? sender, ServerConnectionStateChangedEventArgs e)
    {
        // UI update will happen via status timer
        _logger.LogInformation("OPC UA server {ServerName} ({ServerId}) state changed: {OldState} -> {NewState}",
            e.ServerName, e.ServerId, e.OldState, e.NewState);
    }

    private void OnPersistenceModeChanged(object? sender, PersistenceModeChangedEventArgs e)
    {
        _logger.LogInformation("Persistence mode changed: {Previous} -> {New}", e.PreviousMode, e.NewMode);

        // Show balloon notification for mode changes
        var message = e.NewMode switch
        {
            PersistenceMode.JsonFallback => "Basculé vers JSON (MongoDB indisponible)",
            PersistenceMode.MongoDB => "Retour à MongoDB",
            _ => $"Mode de persistance : {e.NewMode}"
        };

        _notifyIcon.ShowBalloonTip(3000, "Client OPC UA", message, ToolTipIcon.Info);
    }

    private void OnHealthChanged(object? sender, StorageHealth health)
    {
        _logger.LogInformation("MongoDB health changed: {Health}", health);
    }

    private async void OnExit(object? sender, EventArgs e)
    {
        _logger.LogInformation("Application exiting...");

        // Stop status timer
        _statusTimer.Stop();
        _statusTimer.Dispose();

        // Stop acquisition only in standalone mode
        if (!_useWindowsService)
        {
            await StopAcquisitionAsync();

            // Complete channel to drain remaining items
            _channel.Complete();
        }

        // Cleanup
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();

        // Exit application
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _statusTimer?.Dispose();
            _notifyIcon?.Dispose();
            _contextMenu?.Dispose();
            _configForm?.Dispose();
            _acquisitionCts?.Dispose();
            _serviceHelper?.Dispose();

            // Dispose cached icons
            foreach (var icon in _iconCache.Values)
            {
                icon?.Dispose();
            }
            _iconCache.Clear();
        }

        base.Dispose(disposing);
    }
}
