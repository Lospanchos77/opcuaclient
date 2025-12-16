using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpcUaTrayClient.Core.Models;

namespace OpcUaTrayClient.Core.Configuration;

/// <summary>
/// Manages application configuration persistence.
/// Configuration is stored in %AppData%/OpcUaTrayClient/config.json.
/// </summary>
public sealed class ConfigurationService : IDisposable
{
    private readonly ILogger<ConfigurationService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly string _appDataPath;
    private readonly string _configFilePath;
    private readonly string _dataFolderPath;
    private readonly string _archiveFolderPath;
    private readonly string _logsFolderPath;

    private AppConfiguration _current;
    private bool _disposed;

    /// <summary>
    /// Event raised when configuration changes.
    /// </summary>
    public event EventHandler? ConfigurationChanged;

    /// <summary>
    /// Current application configuration.
    /// </summary>
    public AppConfiguration Current => _current;

    /// <summary>
    /// Path to the configuration file.
    /// </summary>
    public string ConfigFilePath => _configFilePath;

    /// <summary>
    /// Path to the data folder for JSON fallback files.
    /// Uses custom path if configured, otherwise default.
    /// </summary>
    public string DataFolderPath => !string.IsNullOrWhiteSpace(_current.JsonFallbackPath)
        ? _current.JsonFallbackPath
        : _dataFolderPath;

    /// <summary>
    /// Path to the archive folder for recovered JSON files.
    /// Uses subfolder of custom path if configured, otherwise default.
    /// </summary>
    public string ArchiveFolderPath => !string.IsNullOrWhiteSpace(_current.JsonFallbackPath)
        ? Path.Combine(_current.JsonFallbackPath, "Archive")
        : _archiveFolderPath;

    /// <summary>
    /// Path to the logs folder.
    /// </summary>
    public string LogsFolderPath => _logsFolderPath;

    public ConfigurationService(ILogger<ConfigurationService> logger)
    {
        _logger = logger;

        // Setup paths
        _appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OpcUaTrayClient");
        _configFilePath = Path.Combine(_appDataPath, "config.json");
        _dataFolderPath = Path.Combine(_appDataPath, "Data");
        _archiveFolderPath = Path.Combine(_dataFolderPath, "Archive");
        _logsFolderPath = Path.Combine(_appDataPath, "Logs");

        // JSON serialization options
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };

        // Initialize with defaults
        _current = new AppConfiguration();

        // Ensure directories exist
        EnsureDirectoriesExist();
    }

    private void EnsureDirectoriesExist()
    {
        try
        {
            Directory.CreateDirectory(_appDataPath);
            Directory.CreateDirectory(_dataFolderPath);
            Directory.CreateDirectory(_archiveFolderPath);
            Directory.CreateDirectory(_logsFolderPath);
            _logger.LogDebug("Application directories ensured at {Path}", _appDataPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create application directories at {Path}", _appDataPath);
            throw;
        }
    }

    /// <summary>
    /// Loads configuration from disk. Creates default if not exists.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _saveLock.WaitAsync(ct);
        try
        {
            if (!File.Exists(_configFilePath))
            {
                _logger.LogInformation("Configuration file not found, creating default at {Path}", _configFilePath);
                _current = new AppConfiguration();
                await SaveInternalAsync(ct);
                return;
            }

            var json = await File.ReadAllTextAsync(_configFilePath, ct);
            var config = JsonSerializer.Deserialize<AppConfiguration>(json, _jsonOptions);

            if (config == null)
            {
                _logger.LogWarning("Configuration file was empty or invalid, using defaults");
                _current = new AppConfiguration();
                await SaveInternalAsync(ct);
                return;
            }

            _current = config;

            // V1.0.0 to V2.0.0 migration: Convert single endpoint to multi-server
            if (await MigrateV1ToV2Async(ct))
            {
                _logger.LogInformation("Configuration migrated from V1.0.0 to V2.0.0 format");
            }

            _logger.LogInformation("Configuration loaded from {Path}", _configFilePath);
            _logger.LogDebug("Loaded config: Servers={ServerCount}, ForceJson={ForceJson}, DryRun={DryRun}",
                _current.Servers.Count, _current.ForceJsonOnly, _current.DryRunMode);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse configuration file, using defaults");
            _current = new AppConfiguration();

            // Backup corrupted config
            try
            {
                var backupPath = _configFilePath + ".corrupted." + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                File.Move(_configFilePath, backupPath);
                _logger.LogInformation("Corrupted config backed up to {Path}", backupPath);
            }
            catch { }

            await SaveInternalAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load configuration from {Path}", _configFilePath);
            throw;
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>
    /// Saves the current configuration to disk.
    /// </summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _saveLock.WaitAsync(ct);
        try
        {
            await SaveInternalAsync(ct);
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>
    /// Migrates V1.0.0 single-server configuration to V2.0.0 multi-server format.
    /// Returns true if migration was performed.
    /// </summary>
    private async Task<bool> MigrateV1ToV2Async(CancellationToken ct)
    {
        // Check if migration is needed: V1 has OpcUaEndpointUrl set but no servers
        if (string.IsNullOrWhiteSpace(_current.OpcUaEndpointUrl) || _current.Servers.Count > 0)
        {
            return false;
        }

        _logger.LogInformation("Migrating V1.0.0 configuration to V2.0.0 multi-server format");

        // Create a server configuration from the legacy single-server settings
        var migratedServer = new OpcUaServerConfiguration
        {
            Id = "default",
            Name = "Serveur par défaut (migré)",
            EndpointUrl = _current.OpcUaEndpointUrl,
            Enabled = true,
            SessionTimeoutMs = null,  // Use global defaults
            KeepAliveIntervalMs = null,  // Use global defaults
            Subscriptions = _current.Subscriptions.ToList()
        };

        _current.Servers.Add(migratedServer);

        // Clear legacy fields to prevent re-migration
        _current.OpcUaEndpointUrl = "";
        _current.Subscriptions.Clear();

        // Save the migrated configuration
        await SaveInternalAsync(ct);

        _logger.LogInformation(
            "Migration complete: created server '{ServerName}' with {SubscriptionCount} subscriptions",
            migratedServer.Name,
            migratedServer.Subscriptions.Count);

        return true;
    }

    private async Task SaveInternalAsync(CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(_current, _jsonOptions);

            // Write to temp file first, then rename for atomic operation
            var tempPath = _configFilePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json, ct);
            File.Move(tempPath, _configFilePath, overwrite: true);

            _logger.LogDebug("Configuration saved to {Path}", _configFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save configuration to {Path}", _configFilePath);
            throw;
        }
    }

    /// <summary>
    /// Updates the configuration and saves it.
    /// </summary>
    public async Task UpdateAsync(Action<AppConfiguration> updateAction, CancellationToken ct = default)
    {
        await _saveLock.WaitAsync(ct);
        try
        {
            updateAction(_current);
            await SaveInternalAsync(ct);
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>
    /// [DEPRECATED - V1.0.0] Adds a subscription to the legacy subscriptions list.
    /// Use AddSubscriptionAsync(serverId, subscription) for V2.0.0.
    /// </summary>
    public async Task AddSubscriptionAsync(SubscriptionDefinition subscription, CancellationToken ct = default)
    {
        await UpdateAsync(config =>
        {
            // Remove existing subscription with same NodeId if present
            config.Subscriptions.RemoveAll(s => s.NodeId == subscription.NodeId);
            config.Subscriptions.Add(subscription);
        }, ct);

        _logger.LogInformation("Subscription added: {NodeId} ({DisplayName})", subscription.NodeId, subscription.DisplayName);
    }

    /// <summary>
    /// [DEPRECATED - V1.0.0] Removes a subscription from the legacy subscriptions list.
    /// Use RemoveSubscriptionAsync(serverId, nodeId) for V2.0.0.
    /// </summary>
    public async Task RemoveSubscriptionAsync(string nodeId, CancellationToken ct = default)
    {
        await UpdateAsync(config =>
        {
            config.Subscriptions.RemoveAll(s => s.NodeId == nodeId);
        }, ct);

        _logger.LogInformation("Subscription removed: {NodeId}", nodeId);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // V2.0.0: Multi-Server Configuration Methods
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// V2.0.0: Adds a server configuration.
    /// </summary>
    public async Task AddServerAsync(OpcUaServerConfiguration server, CancellationToken ct = default)
    {
        await UpdateAsync(config =>
        {
            // Generate ID if not provided
            if (string.IsNullOrEmpty(server.Id))
            {
                server.Id = Guid.NewGuid().ToString("N")[..8];
            }

            // Remove existing server with same ID if present
            config.Servers.RemoveAll(s => s.Id == server.Id);
            config.Servers.Add(server);
        }, ct);

        _logger.LogInformation("Server added: {ServerName} ({ServerId})", server.Name, server.Id);
    }

    /// <summary>
    /// V2.0.0: Removes a server configuration.
    /// </summary>
    public async Task RemoveServerAsync(string serverId, CancellationToken ct = default)
    {
        await UpdateAsync(config =>
        {
            config.Servers.RemoveAll(s => s.Id == serverId);
        }, ct);

        _logger.LogInformation("Server removed: {ServerId}", serverId);
    }

    /// <summary>
    /// V2.0.0: Updates a server configuration.
    /// </summary>
    public async Task UpdateServerAsync(string serverId, Action<OpcUaServerConfiguration> updateAction, CancellationToken ct = default)
    {
        await UpdateAsync(config =>
        {
            var server = config.Servers.FirstOrDefault(s => s.Id == serverId);
            if (server != null)
            {
                updateAction(server);
            }
        }, ct);

        _logger.LogInformation("Server updated: {ServerId}", serverId);
    }

    /// <summary>
    /// V2.0.0: Adds a subscription to a specific server.
    /// </summary>
    public async Task AddSubscriptionAsync(string serverId, SubscriptionDefinition subscription, CancellationToken ct = default)
    {
        await UpdateAsync(config =>
        {
            var server = config.Servers.FirstOrDefault(s => s.Id == serverId);
            if (server != null)
            {
                // Remove existing subscription with same NodeId if present
                server.Subscriptions.RemoveAll(s => s.NodeId == subscription.NodeId);
                server.Subscriptions.Add(subscription);
            }
        }, ct);

        _logger.LogInformation("Subscription added to server {ServerId}: {NodeId} ({DisplayName})",
            serverId, subscription.NodeId, subscription.DisplayName);
    }

    /// <summary>
    /// V2.0.0: Removes a subscription from a specific server.
    /// </summary>
    public async Task RemoveSubscriptionAsync(string serverId, string nodeId, CancellationToken ct = default)
    {
        await UpdateAsync(config =>
        {
            var server = config.Servers.FirstOrDefault(s => s.Id == serverId);
            server?.Subscriptions.RemoveAll(s => s.NodeId == nodeId);
        }, ct);

        _logger.LogInformation("Subscription removed from server {ServerId}: {NodeId}", serverId, nodeId);
    }

    /// <summary>
    /// V2.0.0: Gets a server configuration by ID.
    /// </summary>
    public OpcUaServerConfiguration? GetServer(string serverId)
    {
        return _current.Servers.FirstOrDefault(s => s.Id == serverId);
    }

    /// <summary>
    /// Exports the current configuration to a file.
    /// </summary>
    public async Task ExportAsync(string filePath, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(_current, _jsonOptions);
        await File.WriteAllTextAsync(filePath, json, ct);
        _logger.LogInformation("Configuration exported to {Path}", filePath);
    }

    /// <summary>
    /// Imports configuration from a file.
    /// </summary>
    public async Task ImportAsync(string filePath, CancellationToken ct = default)
    {
        var json = await File.ReadAllTextAsync(filePath, ct);
        var config = JsonSerializer.Deserialize<AppConfiguration>(json, _jsonOptions)
            ?? throw new InvalidOperationException("Invalid configuration file");

        await _saveLock.WaitAsync(ct);
        try
        {
            _current = config;
            await SaveInternalAsync(ct);
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _saveLock.Release();
        }

        _logger.LogInformation("Configuration imported from {Path}", filePath);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _saveLock.Dispose();
    }
}
