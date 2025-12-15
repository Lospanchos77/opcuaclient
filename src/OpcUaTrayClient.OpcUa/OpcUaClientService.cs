using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using OpcUaTrayClient.Core.Channel;
using OpcUaTrayClient.Core.Models;

namespace OpcUaTrayClient.OpcUa;

/// <summary>
/// OPC UA client with automatic reconnection.
///
/// CRITICAL DESIGN:
/// - Data notifications are converted to OpcUaDataPoint and pushed to DataPointChannel
/// - TryWrite() is NON-BLOCKING - acquisition never waits for persistence
/// - Automatic reconnection with exponential backoff
/// - Stores subscription definitions for re-subscription after reconnect
/// </summary>
public sealed class OpcUaClientService : IDisposable
{
    private readonly ILogger<OpcUaClientService> _logger;
    private readonly DataPointChannel _channel;

    private ApplicationConfiguration? _appConfig;
    private Session? _session;
    private Subscription? _subscription;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private CancellationTokenSource? _reconnectCts;

    private readonly TimeSpan _sessionTimeout;
    private readonly TimeSpan _keepAliveInterval;
    private readonly TimeSpan _initialReconnectDelay = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _maxReconnectDelay = TimeSpan.FromSeconds(60);

    private OpcUaConnectionState _connectionState = OpcUaConnectionState.Disconnected;
    private readonly List<SubscriptionDefinition> _subscribedNodes = new();
    private readonly Dictionary<string, string> _browsePathsByNodeId = new();
    private long _totalDataPointsReceived;
    private DateTime _lastDataPointTime;
    private string? _lastError;

    /// <summary>
    /// Current connection state.
    /// </summary>
    public OpcUaConnectionState ConnectionState => _connectionState;

    /// <summary>
    /// Total data points received since connection started.
    /// </summary>
    public long TotalDataPointsReceived => _totalDataPointsReceived;

    /// <summary>
    /// Time of last received data point.
    /// </summary>
    public DateTime LastDataPointTime => _lastDataPointTime;

    /// <summary>
    /// Last error message.
    /// </summary>
    public string? LastError => _lastError;

    /// <summary>
    /// Event raised when connection state changes.
    /// </summary>
    public event EventHandler<OpcUaConnectionState>? ConnectionStateChanged;

    /// <summary>
    /// Event raised when a data point is received (for UI display).
    /// </summary>
    public event EventHandler<OpcUaDataPoint>? DataPointReceived;

    public OpcUaClientService(
        DataPointChannel channel,
        ILogger<OpcUaClientService> logger,
        int sessionTimeoutMs = 600000,
        int keepAliveIntervalMs = 5000)
    {
        _channel = channel;
        _logger = logger;
        _sessionTimeout = TimeSpan.FromMilliseconds(sessionTimeoutMs);
        _keepAliveInterval = TimeSpan.FromMilliseconds(keepAliveIntervalMs);
    }

    /// <summary>
    /// Connects to the OPC UA server.
    /// </summary>
    public async Task ConnectAsync(string endpointUrl, CancellationToken ct = default)
    {
        await _connectionLock.WaitAsync(ct);
        try
        {
            SetConnectionState(OpcUaConnectionState.Connecting);
            _lastError = null;

            // Create application configuration
            _appConfig = new ApplicationConfiguration
            {
                ApplicationName = "OpcUaTrayClient",
                ApplicationType = ApplicationType.Client,
                SecurityConfiguration = new SecurityConfiguration
                {
                    ApplicationCertificate = new CertificateIdentifier(),
                    AutoAcceptUntrustedCertificates = true, // For development - configure properly for production
                    RejectSHA1SignedCertificates = false,
                    MinimumCertificateKeySize = 1024
                },
                ClientConfiguration = new ClientConfiguration
                {
                    DefaultSessionTimeout = (int)_sessionTimeout.TotalMilliseconds
                },
                TransportQuotas = new TransportQuotas
                {
                    OperationTimeout = 30000,
                    MaxStringLength = 1048576,
                    MaxByteStringLength = 4194304,
                    MaxArrayLength = 65535,
                    MaxMessageSize = 4194304
                }
            };

            await _appConfig.Validate(ApplicationType.Client);

            // Select endpoint (no security for now)
            var endpoint = CoreClientUtils.SelectEndpoint(endpointUrl, useSecurity: false);
            var endpointConfig = EndpointConfiguration.Create(_appConfig);
            var configuredEndpoint = new ConfiguredEndpoint(null, endpoint, endpointConfig);

            // Create session
            _session = await Session.Create(
                _appConfig,
                configuredEndpoint,
                updateBeforeConnect: false,
                sessionName: "OpcUaTrayClient_Session",
                sessionTimeout: (uint)_sessionTimeout.TotalMilliseconds,
                identity: null, // Anonymous
                preferredLocales: null,
                ct
            );

            // Configure keepalive
            _session.KeepAliveInterval = (int)_keepAliveInterval.TotalMilliseconds;
            _session.KeepAlive += OnSessionKeepAlive;
            _session.SessionClosing += OnSessionClosing;

            SetConnectionState(OpcUaConnectionState.Connected);
            _logger.LogInformation("Connected to OPC UA server: {Endpoint}", endpointUrl);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            SetConnectionState(OpcUaConnectionState.Error);
            _logger.LogError(ex, "Failed to connect to OPC UA server: {Endpoint}", endpointUrl);
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// Disconnects from the OPC UA server.
    /// </summary>
    public async Task DisconnectAsync()
    {
        _reconnectCts?.Cancel();

        await _connectionLock.WaitAsync();
        try
        {
            if (_subscription != null)
            {
                try
                {
                    _subscription.Delete(silent: true);
                }
                catch { }
                _subscription = null;
            }

            if (_session != null)
            {
                try
                {
                    _session.KeepAlive -= OnSessionKeepAlive;
                    _session.SessionClosing -= OnSessionClosing;
                    _session.Close();
                    _session.Dispose();
                }
                catch { }
                _session = null;
            }

            SetConnectionState(OpcUaConnectionState.Disconnected);
            _logger.LogInformation("Disconnected from OPC UA server");
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// Browses the OPC UA address space from a starting node.
    /// </summary>
    public async Task<List<OpcUaNode>> BrowseAsync(string? startNodeId = null, CancellationToken ct = default)
    {
        if (_session == null || !_session.Connected)
        {
            throw new InvalidOperationException("Not connected to OPC UA server");
        }

        var nodeId = string.IsNullOrEmpty(startNodeId)
            ? ObjectIds.ObjectsFolder
            : NodeId.Parse(startNodeId);

        var browseDescription = new BrowseDescription
        {
            NodeId = nodeId,
            BrowseDirection = BrowseDirection.Forward,
            ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
            IncludeSubtypes = true,
            NodeClassMask = 0, // All node classes
            ResultMask = (uint)BrowseResultMask.All
        };

        var results = new List<OpcUaNode>();

        try
        {
            _session.Browse(
                null,
                null,
                0,
                new BrowseDescriptionCollection { browseDescription },
                out var browseResults,
                out var diagnostics);

            if (browseResults?.Count > 0 && browseResults[0].References != null)
            {
                foreach (var reference in browseResults[0].References)
                {
                    var node = new OpcUaNode
                    {
                        NodeId = reference.NodeId.ToString(),
                        DisplayName = reference.DisplayName.Text ?? reference.BrowseName.Name ?? "Unknown",
                        BrowseName = reference.BrowseName.ToString(),
                        NodeClass = (OpcUaNodeClass)(int)reference.NodeClass,
                        HasChildren = true, // Assume has children until proven otherwise
                        NamespaceIndex = reference.NodeId.NamespaceIndex
                    };

                    // Check if it's a Variable and get data type
                    if (node.NodeClass == OpcUaNodeClass.Variable)
                    {
                        try
                        {
                            var dataType = await ReadDataTypeAsync(reference.NodeId, ct);
                            node.DataType = dataType;
                            node.IsSubscribable = true;
                        }
                        catch
                        {
                            node.IsSubscribable = false;
                        }
                    }

                    results.Add(node);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Browse failed for node {NodeId}", nodeId);
            throw;
        }

        return results;
    }

    private async Task<string> ReadDataTypeAsync(ExpandedNodeId nodeId, CancellationToken ct)
    {
        if (_session == null) return "Unknown";

        var nodesToRead = new ReadValueIdCollection
        {
            new ReadValueId
            {
                NodeId = ExpandedNodeId.ToNodeId(nodeId, _session.NamespaceUris),
                AttributeId = Attributes.DataType
            }
        };

        _session.Read(
            null,
            0,
            TimestampsToReturn.Neither,
            nodesToRead,
            out var results,
            out var diagnostics);

        if (results?.Count > 0 && StatusCode.IsGood(results[0].StatusCode))
        {
            var dataTypeId = results[0].Value as NodeId;
            if (dataTypeId != null)
            {
                // Try to get the data type name
                var typeNode = await _session.ReadNodeAsync(dataTypeId, ct);
                return typeNode?.DisplayName.Text ?? dataTypeId.ToString();
            }
        }

        return "Unknown";
    }

    /// <summary>
    /// Subscribes to the specified nodes for data change notifications.
    /// </summary>
    public async Task SubscribeToNodesAsync(IEnumerable<SubscriptionDefinition> definitions, CancellationToken ct = default)
    {
        if (_session == null || !_session.Connected)
        {
            throw new InvalidOperationException("Not connected to OPC UA server");
        }

        var defList = definitions.Where(d => d.Enabled).ToList();
        if (defList.Count == 0)
        {
            _logger.LogInformation("No enabled subscriptions to create");
            return;
        }

        // Store for re-subscription after reconnect
        lock (_subscribedNodes)
        {
            _subscribedNodes.Clear();
            _subscribedNodes.AddRange(defList);

            // Build NodeId to BrowsePath mapping for notification handler
            _browsePathsByNodeId.Clear();
            foreach (var def in defList)
            {
                _browsePathsByNodeId[def.NodeId] = def.BrowsePath;
            }
        }

        // Create subscription
        _subscription = new Subscription(_session.DefaultSubscription)
        {
            PublishingInterval = defList.Min(d => d.PublishingIntervalMs),
            KeepAliveCount = 10,
            LifetimeCount = 100,
            MaxNotificationsPerPublish = 1000,
            PublishingEnabled = true
        };

        foreach (var def in defList)
        {
            var monitoredItem = new MonitoredItem(_subscription.DefaultItem)
            {
                StartNodeId = def.NodeId,
                DisplayName = def.DisplayName,
                AttributeId = Attributes.Value,
                SamplingInterval = def.SamplingIntervalMs,
                QueueSize = def.QueueSize,
                DiscardOldest = def.DiscardOldest
            };

            monitoredItem.Notification += OnDataNotification;
            _subscription.AddItem(monitoredItem);
        }

        _session.AddSubscription(_subscription);
        await _subscription.CreateAsync(ct);

        _logger.LogInformation("Subscribed to {Count} nodes", defList.Count);
    }

    /// <summary>
    /// Removes all subscriptions.
    /// </summary>
    public async Task UnsubscribeAllAsync(CancellationToken ct = default)
    {
        if (_subscription == null) return;

        try
        {
            await _subscription.DeleteAsync(silent: true);
            _subscription = null;

            lock (_subscribedNodes)
            {
                _subscribedNodes.Clear();
                _browsePathsByNodeId.Clear();
            }

            _logger.LogInformation("All subscriptions removed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove subscriptions");
        }
    }

    private void OnDataNotification(MonitoredItem item, MonitoredItemNotificationEventArgs e)
    {
        try
        {
            // Look up the browse path for this node
            var nodeId = item.StartNodeId.ToString();
            string? browsePath = null;
            lock (_subscribedNodes)
            {
                if (!_browsePathsByNodeId.TryGetValue(nodeId, out browsePath))
                {
                    // Try alternative format (OPC UA SDK may format NodeId differently)
                    var resolvedNodeId = item.ResolvedNodeId?.ToString();
                    if (resolvedNodeId != null)
                    {
                        _browsePathsByNodeId.TryGetValue(resolvedNodeId, out browsePath);
                    }
                }
            }

            foreach (var value in item.DequeueValues())
            {
                var finalBrowsePath = !string.IsNullOrEmpty(browsePath) ? browsePath : item.DisplayName;

                var dataPoint = new OpcUaDataPoint
                {
                    TimestampUtc = DateTime.UtcNow,
                    NodeId = nodeId,
                    DisplayName = item.DisplayName,
                    BrowsePath = finalBrowsePath,
                    DataType = value.Value?.GetType().Name ?? "Unknown",
                    Value = value.Value,
                    StatusCode = value.StatusCode.Code,
                    Quality = value.StatusCode.ToString(),
                    SourceTimestamp = value.SourceTimestamp,
                    ServerTimestamp = value.ServerTimestamp
                };

                // Non-blocking write to channel - CRITICAL: never blocks OPC UA
                _channel.TryWrite(dataPoint);

                // Update statistics
                Interlocked.Increment(ref _totalDataPointsReceived);
                _lastDataPointTime = DateTime.UtcNow;

                // Raise event for UI display
                DataPointReceived?.Invoke(this, dataPoint);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing data notification for {NodeId}", item.StartNodeId);
        }
    }

    private void OnSessionKeepAlive(ISession session, KeepAliveEventArgs e)
    {
        if (ServiceResult.IsBad(e.Status))
        {
            _lastError = e.Status.ToString();
            _logger.LogWarning("Session keepalive failed: {Status}", e.Status);

            if (e.Status.Code == StatusCodes.BadSessionIdInvalid ||
                e.Status.Code == StatusCodes.BadConnectionClosed ||
                e.Status.Code == StatusCodes.BadCommunicationError)
            {
                StartReconnection();
            }
        }
    }

    private void OnSessionClosing(object? sender, EventArgs e)
    {
        _logger.LogWarning("Session closing unexpectedly");
        StartReconnection();
    }

    private void StartReconnection()
    {
        if (_connectionState == OpcUaConnectionState.Reconnecting) return;

        _reconnectCts?.Cancel();
        _reconnectCts = new CancellationTokenSource();

        _ = ReconnectLoopAsync(_reconnectCts.Token);
    }

    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        SetConnectionState(OpcUaConnectionState.Reconnecting);

        var attempt = 0;
        var delay = _initialReconnectDelay;

        while (!ct.IsCancellationRequested)
        {
            attempt++;
            _logger.LogInformation("Reconnection attempt {Attempt}", attempt);

            try
            {
                // Clean up old session
                await CleanupSessionAsync();

                // Get endpoint URL from old session
                var endpointUrl = _session?.Endpoint?.EndpointUrl ?? "opc.tcp://localhost:53530/OPCUA/SimulationServer";

                // Reconnect
                await ConnectAsync(endpointUrl, ct);

                // Re-subscribe to nodes
                List<SubscriptionDefinition> nodesToSubscribe;
                lock (_subscribedNodes)
                {
                    nodesToSubscribe = _subscribedNodes.ToList();
                }

                if (nodesToSubscribe.Count > 0)
                {
                    await SubscribeToNodesAsync(nodesToSubscribe, ct);
                }

                _logger.LogInformation("Reconnection successful after {Attempts} attempts", attempt);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _logger.LogWarning(ex, "Reconnection attempt {Attempt} failed", attempt);

                // Exponential backoff with cap
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, _maxReconnectDelay.TotalSeconds));
            }
        }

        SetConnectionState(OpcUaConnectionState.Error);
        _logger.LogError("Reconnection abandoned");
    }

    private async Task CleanupSessionAsync()
    {
        try
        {
            if (_subscription != null)
            {
                _subscription.Delete(silent: true);
                _subscription = null;
            }

            if (_session != null)
            {
                _session.KeepAlive -= OnSessionKeepAlive;
                _session.SessionClosing -= OnSessionClosing;
                _session.Close();
                _session.Dispose();
                _session = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error during session cleanup");
        }

        await Task.CompletedTask;
    }

    private void SetConnectionState(OpcUaConnectionState newState)
    {
        if (_connectionState != newState)
        {
            _connectionState = newState;
            _logger.LogInformation("OPC UA connection state: {State}", newState);
            ConnectionStateChanged?.Invoke(this, newState);
        }
    }

    public void Dispose()
    {
        _reconnectCts?.Cancel();
        _reconnectCts?.Dispose();
        _subscription?.Delete(silent: true);
        _session?.Close();
        _session?.Dispose();
        _connectionLock.Dispose();
    }
}
