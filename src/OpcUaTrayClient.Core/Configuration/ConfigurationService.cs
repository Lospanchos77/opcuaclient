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
            _logger.LogInformation("Configuration loaded from {Path}", _configFilePath);
            _logger.LogDebug("Loaded config: Endpoint={Endpoint}, ForceJson={ForceJson}, DryRun={DryRun}",
                _current.OpcUaEndpointUrl, _current.ForceJsonOnly, _current.DryRunMode);
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
    /// Adds a subscription to the configuration.
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
    /// Removes a subscription from the configuration.
    /// </summary>
    public async Task RemoveSubscriptionAsync(string nodeId, CancellationToken ct = default)
    {
        await UpdateAsync(config =>
        {
            config.Subscriptions.RemoveAll(s => s.NodeId == nodeId);
        }, ct);

        _logger.LogInformation("Subscription removed: {NodeId}", nodeId);
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
