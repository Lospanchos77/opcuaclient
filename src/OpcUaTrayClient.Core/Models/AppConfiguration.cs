using System.Text.Json.Serialization;

namespace OpcUaTrayClient.Core.Models;

/// <summary>
/// Complete application configuration. Persisted to %AppData%/OpcUaTrayClient/config.json.
/// All settings are hot-reloadable except those marked otherwise.
/// </summary>
public sealed class AppConfiguration
{
    // ─────────────────────────────────────────────────────────────────────────
    // OPC UA CONNECTION
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// OPC UA server endpoint URL.
    /// Example: opc.tcp://localhost:53530/OPCUA/SimulationServer
    /// </summary>
    [JsonPropertyName("opcUaEndpointUrl")]
    public string OpcUaEndpointUrl { get; set; } = "opc.tcp://localhost:53530/OPCUA/SimulationServer";

    /// <summary>
    /// Session timeout in milliseconds. The session will be recreated if idle for this long.
    /// </summary>
    [JsonPropertyName("sessionTimeoutMs")]
    public int SessionTimeoutMs { get; set; } = 600_000; // 10 minutes

    /// <summary>
    /// Keepalive interval in milliseconds. Sent to detect connection loss.
    /// </summary>
    [JsonPropertyName("keepAliveIntervalMs")]
    public int KeepAliveIntervalMs { get; set; } = 5_000; // 5 seconds

    // ─────────────────────────────────────────────────────────────────────────
    // MONGODB PRIMARY STORAGE
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// MongoDB connection string. Supports replica sets, authentication, SSL.
    /// Example: mongodb://user:pass@server1:27017,server2:27017/dbname?replicaSet=rs0
    /// </summary>
    [JsonPropertyName("mongoConnectionString")]
    public string MongoConnectionString { get; set; } = "mongodb://localhost:27017";

    /// <summary>
    /// MongoDB database name.
    /// </summary>
    [JsonPropertyName("mongoDatabaseName")]
    public string MongoDatabaseName { get; set; } = "opcua_data";

    /// <summary>
    /// MongoDB collection name for storing data points.
    /// </summary>
    [JsonPropertyName("mongoCollectionName")]
    public string MongoCollectionName { get; set; } = "datapoints";

    /// <summary>
    /// Interval between MongoDB health checks in seconds.
    /// Lower values = faster failover detection, but more overhead.
    /// </summary>
    [JsonPropertyName("mongoHealthCheckIntervalSeconds")]
    public int MongoHealthCheckIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// MongoDB connection/operation timeout in seconds.
    /// </summary>
    [JsonPropertyName("mongoTimeoutSeconds")]
    public int MongoTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Number of data points to batch before inserting to MongoDB.
    /// Higher values = better throughput, but higher latency and memory.
    /// </summary>
    [JsonPropertyName("mongoBatchSize")]
    public int MongoBatchSize { get; set; } = 100;

    /// <summary>
    /// Maximum time to wait before flushing a partial batch, in milliseconds.
    /// </summary>
    [JsonPropertyName("mongoBatchTimeoutMs")]
    public int MongoBatchTimeoutMs { get; set; } = 1000;

    /// <summary>
    /// Time-to-live for data points in MongoDB, in days. Set to 0 to disable.
    /// Uses MongoDB TTL index for automatic cleanup.
    /// </summary>
    [JsonPropertyName("mongoTtlDays")]
    public int MongoTtlDays { get; set; } = 30;

    // ─────────────────────────────────────────────────────────────────────────
    // JSON FALLBACK STORAGE
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Custom path for JSON fallback storage. If empty, uses default %AppData%/OpcUaTrayClient/Data.
    /// </summary>
    [JsonPropertyName("jsonFallbackPath")]
    public string JsonFallbackPath { get; set; } = "";

    /// <summary>
    /// Maximum size of a single JSON fallback file before rolling to a new file, in MB.
    /// </summary>
    [JsonPropertyName("jsonMaxFileSizeMb")]
    public int JsonMaxFileSizeMb { get; set; } = 50;

    /// <summary>
    /// Maximum total size of JSON fallback files before oldest are deleted, in MB.
    /// Set to 0 to disable automatic cleanup.
    /// </summary>
    [JsonPropertyName("jsonMaxTotalSizeMb")]
    public int JsonMaxTotalSizeMb { get; set; } = 1000;

    // ─────────────────────────────────────────────────────────────────────────
    // PERSISTENCE MODES
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Force JSON-only mode. MongoDB will not be used even if available.
    /// Useful for diagnostics or when MongoDB is under maintenance.
    /// </summary>
    [JsonPropertyName("forceJsonOnly")]
    public bool ForceJsonOnly { get; set; } = false;

    /// <summary>
    /// Dry-run mode. Data is acquired but not persisted anywhere.
    /// Useful for testing OPC UA connectivity without affecting storage.
    /// </summary>
    [JsonPropertyName("dryRunMode")]
    public bool DryRunMode { get; set; } = false;

    // ─────────────────────────────────────────────────────────────────────────
    // CHANNEL / THROTTLING
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Maximum capacity of the internal data point channel.
    /// When full, oldest points are dropped to prevent blocking OPC UA acquisition.
    /// </summary>
    [JsonPropertyName("channelCapacity")]
    public int ChannelCapacity { get; set; } = 10_000;

    /// <summary>
    /// Maximum data points to process per second. Set to 0 for unlimited.
    /// Use to limit load on persistence layer during high-frequency updates.
    /// </summary>
    [JsonPropertyName("maxPointsPerSecond")]
    public int MaxPointsPerSecond { get; set; } = 0;

    // ─────────────────────────────────────────────────────────────────────────
    // SUBSCRIPTIONS
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// List of OPC UA node subscriptions.
    /// </summary>
    [JsonPropertyName("subscriptions")]
    public List<SubscriptionDefinition> Subscriptions { get; set; } = new();

    // ─────────────────────────────────────────────────────────────────────────
    // FILTERING
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Namespace indices to ignore when browsing (e.g., 0 for OPC UA standard namespace).
    /// </summary>
    [JsonPropertyName("ignoredNamespaces")]
    public List<int> IgnoredNamespaces { get; set; } = new() { 0 };

    /// <summary>
    /// Node display name patterns to filter out (case-insensitive contains match).
    /// </summary>
    [JsonPropertyName("nodeNameFilters")]
    public List<string> NodeNameFilters { get; set; } = new();

    // ─────────────────────────────────────────────────────────────────────────
    // APPLICATION
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether to start minimized to system tray.
    /// </summary>
    [JsonPropertyName("startMinimized")]
    public bool StartMinimized { get; set; } = true;

    /// <summary>
    /// Whether to automatically start acquisition on application startup.
    /// </summary>
    [JsonPropertyName("autoStartAcquisition")]
    public bool AutoStartAcquisition { get; set; } = false;

    /// <summary>
    /// Log level: Trace, Debug, Information, Warning, Error, Critical.
    /// </summary>
    [JsonPropertyName("logLevel")]
    public string LogLevel { get; set; } = "Information";

    /// <summary>
    /// Maximum number of log entries to keep in memory for UI display.
    /// </summary>
    [JsonPropertyName("maxLogEntries")]
    public int MaxLogEntries { get; set; } = 1000;
}
