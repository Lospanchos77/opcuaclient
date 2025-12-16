using Microsoft.Extensions.Logging;
using OpcUaTrayClient.Core.Channel;
using OpcUaTrayClient.Core.Models;

namespace OpcUaTrayClient.OpcUa;

/// <summary>
/// Event arguments for server connection state changes.
/// </summary>
public sealed class ServerConnectionStateChangedEventArgs : EventArgs
{
    public string ServerId { get; }
    public string ServerName { get; }
    public OpcUaConnectionState OldState { get; }
    public OpcUaConnectionState NewState { get; }

    public ServerConnectionStateChangedEventArgs(
        string serverId,
        string serverName,
        OpcUaConnectionState oldState,
        OpcUaConnectionState newState)
    {
        ServerId = serverId;
        ServerName = serverName;
        OldState = oldState;
        NewState = newState;
    }
}

/// <summary>
/// Manages multiple OPC UA client connections (V2.0.0 multi-server support).
/// Coordinates connection lifecycle, aggregates statistics, and routes events.
/// </summary>
public sealed class OpcUaClientManager : IDisposable
{
    private readonly ILogger<OpcUaClientManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly DataPointChannel _channel;
    private readonly int _defaultSessionTimeoutMs;
    private readonly int _defaultKeepAliveIntervalMs;

    private readonly Dictionary<string, OpcUaClientService> _clients = new();
    private readonly Dictionary<string, OpcUaConnectionState> _previousStates = new();
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Event raised when any server's connection state changes.
    /// </summary>
    public event EventHandler<ServerConnectionStateChangedEventArgs>? ServerConnectionStateChanged;

    /// <summary>
    /// Event raised when a data point is received from any server.
    /// </summary>
    public event EventHandler<OpcUaDataPoint>? DataPointReceived;

    /// <summary>
    /// Gets the total number of data points received across all servers.
    /// </summary>
    public long TotalDataPointsReceived
    {
        get
        {
            lock (_lock)
            {
                return _clients.Values.Sum(c => c.TotalDataPointsReceived);
            }
        }
    }

    /// <summary>
    /// Gets the most recent data point time across all servers.
    /// </summary>
    public DateTime LastDataPointTime
    {
        get
        {
            lock (_lock)
            {
                if (_clients.Count == 0) return DateTime.MinValue;
                return _clients.Values.Max(c => c.LastDataPointTime);
            }
        }
    }

    /// <summary>
    /// Gets the number of connected servers.
    /// </summary>
    public int ConnectedServerCount
    {
        get
        {
            lock (_lock)
            {
                return _clients.Values.Count(c => c.ConnectionState == OpcUaConnectionState.Connected);
            }
        }
    }

    /// <summary>
    /// Gets the total number of managed servers.
    /// </summary>
    public int TotalServerCount
    {
        get
        {
            lock (_lock)
            {
                return _clients.Count;
            }
        }
    }

    public OpcUaClientManager(
        DataPointChannel channel,
        ILoggerFactory loggerFactory,
        int defaultSessionTimeoutMs = 600000,
        int defaultKeepAliveIntervalMs = 5000)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<OpcUaClientManager>();
        _defaultSessionTimeoutMs = defaultSessionTimeoutMs;
        _defaultKeepAliveIntervalMs = defaultKeepAliveIntervalMs;
    }

    /// <summary>
    /// Connects to all enabled servers in the provided configurations.
    /// </summary>
    public async Task ConnectAllAsync(IEnumerable<OpcUaServerConfiguration> configs, CancellationToken ct = default)
    {
        var enabledConfigs = configs.Where(c => c.Enabled).ToList();
        _logger.LogInformation("Connecting to {Count} OPC UA servers", enabledConfigs.Count);

        var tasks = new List<Task>();

        foreach (var config in enabledConfigs)
        {
            tasks.Add(AddAndConnectServerAsync(config, ct));
        }

        // Wait for all connections (don't fail if some servers fail)
        // Use try-catch per task to continue even if some fail
        foreach (var task in tasks)
        {
            try
            {
                await task;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A server connection failed, continuing with others");
            }
        }

        var connected = ConnectedServerCount;
        var total = TotalServerCount;
        _logger.LogInformation("Connected to {Connected}/{Total} OPC UA servers", connected, total);
    }

    /// <summary>
    /// Adds a new server and connects to it.
    /// </summary>
    public async Task AddServerAsync(OpcUaServerConfiguration config, CancellationToken ct = default)
    {
        await AddAndConnectServerAsync(config, ct);
    }

    private async Task AddAndConnectServerAsync(OpcUaServerConfiguration config, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(config.Id))
        {
            throw new ArgumentException("Server configuration must have an Id", nameof(config));
        }

        OpcUaClientService client;

        lock (_lock)
        {
            if (_clients.TryGetValue(config.Id, out var existingClient))
            {
                // Server exists - check if we need to reconnect
                if (existingClient.ConnectionState == OpcUaConnectionState.Connected)
                {
                    _logger.LogDebug("Server {ServerId} already connected, skipping", config.Id);
                    return;
                }

                _logger.LogInformation("Server {ServerId} exists but disconnected, will reconnect", config.Id);
                client = existingClient;
            }
            else
            {
                // Create new client
                var sessionTimeout = config.SessionTimeoutMs ?? _defaultSessionTimeoutMs;
                var keepAliveInterval = config.KeepAliveIntervalMs ?? _defaultKeepAliveIntervalMs;

                client = new OpcUaClientService(
                    config.Id,
                    config.Name,
                    _channel,
                    _loggerFactory.CreateLogger<OpcUaClientService>(),
                    sessionTimeout,
                    keepAliveInterval);

                client.ConnectionStateChanged += OnClientConnectionStateChanged;
                client.DataPointReceived += OnClientDataPointReceived;

                _clients[config.Id] = client;
                _previousStates[config.Id] = OpcUaConnectionState.Disconnected;
            }
        }

        try
        {
            _logger.LogInformation("Connecting to server {ServerName} ({ServerId}): {Endpoint}",
                config.Name, config.Id, config.EndpointUrl);

            await client.ConnectAsync(config.EndpointUrl, ct);

            // Subscribe to nodes if any
            if (config.Subscriptions.Count > 0)
            {
                await client.SubscribeToNodesAsync(config.Subscriptions, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to server {ServerName} ({ServerId})",
                config.Name, config.Id);
            // Don't remove the client - it may reconnect automatically
        }
    }

    /// <summary>
    /// Removes a server by its ID.
    /// </summary>
    public async Task RemoveServerAsync(string serverId, CancellationToken ct = default)
    {
        OpcUaClientService? client;

        lock (_lock)
        {
            if (!_clients.TryGetValue(serverId, out client))
            {
                _logger.LogWarning("Server {ServerId} not found", serverId);
                return;
            }

            _clients.Remove(serverId);
            _previousStates.Remove(serverId);
        }

        try
        {
            client.ConnectionStateChanged -= OnClientConnectionStateChanged;
            client.DataPointReceived -= OnClientDataPointReceived;
            await client.DisconnectAsync();
            client.Dispose();

            _logger.LogInformation("Server {ServerId} removed", serverId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing server {ServerId}", serverId);
        }
    }

    /// <summary>
    /// Disconnects all servers.
    /// </summary>
    public async Task DisconnectAllAsync()
    {
        _logger.LogInformation("Disconnecting all OPC UA servers");

        List<OpcUaClientService> clients;
        lock (_lock)
        {
            clients = _clients.Values.ToList();
        }

        var tasks = clients.Select(c => c.DisconnectAsync()).ToList();
        await Task.WhenAll(tasks);

        _logger.LogInformation("All OPC UA servers disconnected");
    }

    /// <summary>
    /// Gets the connection state for a specific server.
    /// </summary>
    public OpcUaConnectionState GetServerState(string serverId)
    {
        lock (_lock)
        {
            return _clients.TryGetValue(serverId, out var client)
                ? client.ConnectionState
                : OpcUaConnectionState.Disconnected;
        }
    }

    /// <summary>
    /// Gets all server states.
    /// </summary>
    public IReadOnlyDictionary<string, OpcUaConnectionState> GetAllServerStates()
    {
        lock (_lock)
        {
            return _clients.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.ConnectionState);
        }
    }

    /// <summary>
    /// Gets a client by server ID (for browsing, etc.).
    /// </summary>
    public OpcUaClientService? GetClient(string serverId)
    {
        lock (_lock)
        {
            return _clients.TryGetValue(serverId, out var client) ? client : null;
        }
    }

    /// <summary>
    /// Gets all server IDs.
    /// </summary>
    public IReadOnlyList<string> GetServerIds()
    {
        lock (_lock)
        {
            return _clients.Keys.ToList();
        }
    }

    /// <summary>
    /// Gets aggregated connection state (worst state among all servers).
    /// </summary>
    public OpcUaConnectionState GetAggregatedState()
    {
        lock (_lock)
        {
            if (_clients.Count == 0)
                return OpcUaConnectionState.Disconnected;

            var states = _clients.Values.Select(c => c.ConnectionState).ToList();

            // Priority: Error > Reconnecting > Connecting > Disconnected > Connected
            if (states.Any(s => s == OpcUaConnectionState.Error))
                return OpcUaConnectionState.Error;
            if (states.Any(s => s == OpcUaConnectionState.Reconnecting))
                return OpcUaConnectionState.Reconnecting;
            if (states.Any(s => s == OpcUaConnectionState.Connecting))
                return OpcUaConnectionState.Connecting;
            if (states.Any(s => s == OpcUaConnectionState.Disconnected))
                return OpcUaConnectionState.Disconnected;

            return OpcUaConnectionState.Connected;
        }
    }

    private void OnClientConnectionStateChanged(object? sender, OpcUaConnectionState newState)
    {
        if (sender is not OpcUaClientService client) return;

        OpcUaConnectionState oldState;
        lock (_lock)
        {
            _previousStates.TryGetValue(client.ServerId, out oldState);
            _previousStates[client.ServerId] = newState;
        }

        _logger.LogDebug("Server {ServerName} ({ServerId}) state changed: {OldState} -> {NewState}",
            client.ServerName, client.ServerId, oldState, newState);

        ServerConnectionStateChanged?.Invoke(this, new ServerConnectionStateChangedEventArgs(
            client.ServerId,
            client.ServerName,
            oldState,
            newState));
    }

    private void OnClientDataPointReceived(object? sender, OpcUaDataPoint dataPoint)
    {
        DataPointReceived?.Invoke(this, dataPoint);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_lock)
        {
            foreach (var client in _clients.Values)
            {
                try
                {
                    client.ConnectionStateChanged -= OnClientConnectionStateChanged;
                    client.DataPointReceived -= OnClientDataPointReceived;
                    client.Dispose();
                }
                catch { }
            }
            _clients.Clear();
            _previousStates.Clear();
        }
    }
}
