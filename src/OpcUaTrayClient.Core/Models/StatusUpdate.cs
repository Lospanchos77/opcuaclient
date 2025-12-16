namespace OpcUaTrayClient.Core.Models;

/// <summary>
/// Aggregated status information for UI updates.
/// Sent via IProgress&lt;StatusUpdate&gt; to the UI thread.
/// </summary>
public sealed record StatusUpdate
{
    /// <summary>
    /// Current depth of the data point channel queue.
    /// </summary>
    public int QueueDepth { get; init; }

    /// <summary>
    /// Total number of data points dropped due to channel overflow.
    /// </summary>
    public int DroppedCount { get; init; }

    /// <summary>
    /// Name of the currently active persistence sink (MongoDB/JSON/None).
    /// </summary>
    public string ActiveSink { get; init; } = string.Empty;

    /// <summary>
    /// Current health status of MongoDB.
    /// </summary>
    public StorageHealth MongoHealth { get; init; }

    /// <summary>
    /// Current persistence mode.
    /// </summary>
    public PersistenceMode PersistenceMode { get; init; }

    /// <summary>
    /// Data points received per second (rolling average).
    /// </summary>
    public int DataPointsPerSecond { get; init; }

    /// <summary>
    /// Total data points received since acquisition started.
    /// </summary>
    public long TotalDataPointsReceived { get; init; }

    /// <summary>
    /// Total data points persisted to storage.
    /// </summary>
    public long TotalDataPointsPersisted { get; init; }

    /// <summary>
    /// OPC UA connection state.
    /// </summary>
    public OpcUaConnectionState ConnectionState { get; init; }

    /// <summary>
    /// Last error message, if any.
    /// </summary>
    public string? LastError { get; init; }

    /// <summary>
    /// Timestamp of the last received data point.
    /// </summary>
    public DateTime? LastDataPointTime { get; init; }

    /// <summary>
    /// V2.0.0: Number of currently connected OPC UA servers.
    /// </summary>
    public int ConnectedServerCount { get; init; }

    /// <summary>
    /// V2.0.0: Total number of configured OPC UA servers.
    /// </summary>
    public int TotalServerCount { get; init; }
}

/// <summary>
/// MongoDB health status.
/// </summary>
public enum StorageHealth
{
    /// <summary>
    /// MongoDB is reachable and responding normally.
    /// </summary>
    Healthy,

    /// <summary>
    /// MongoDB is reachable but responding slowly.
    /// </summary>
    Degraded,

    /// <summary>
    /// MongoDB is unreachable or not responding.
    /// </summary>
    Unhealthy,

    /// <summary>
    /// Health status is not yet determined.
    /// </summary>
    Unknown
}

/// <summary>
/// Current persistence mode.
/// </summary>
public enum PersistenceMode
{
    /// <summary>
    /// Using MongoDB as the primary storage.
    /// </summary>
    MongoDB,

    /// <summary>
    /// Using JSON files as fallback storage (MongoDB unavailable).
    /// </summary>
    JsonFallback,

    /// <summary>
    /// Dry-run mode: acquisition active but no persistence.
    /// </summary>
    DryRun,

    /// <summary>
    /// Persistence is stopped.
    /// </summary>
    Stopped
}

/// <summary>
/// OPC UA connection state.
/// </summary>
public enum OpcUaConnectionState
{
    /// <summary>
    /// Not connected to any OPC UA server.
    /// </summary>
    Disconnected,

    /// <summary>
    /// Attempting to connect to the OPC UA server.
    /// </summary>
    Connecting,

    /// <summary>
    /// Connected to the OPC UA server.
    /// </summary>
    Connected,

    /// <summary>
    /// Connection lost, attempting to reconnect.
    /// </summary>
    Reconnecting,

    /// <summary>
    /// Connection error, manual intervention required.
    /// </summary>
    Error
}
