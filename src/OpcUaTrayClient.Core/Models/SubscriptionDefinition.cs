using System.Text.Json.Serialization;

namespace OpcUaTrayClient.Core.Models;

/// <summary>
/// Defines an OPC UA subscription configuration for a specific node.
/// Stored in the application configuration and used to create monitored items.
/// </summary>
public sealed class SubscriptionDefinition
{
    /// <summary>
    /// OPC UA NodeId as string (e.g., "ns=2;s=Demo.Dynamic.Scalar.Int32").
    /// </summary>
    [JsonPropertyName("nodeId")]
    public string NodeId { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable display name for the node.
    /// </summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Full hierarchical browse path (e.g., "Simulation/Counter/ValueSimulation/Min Value").
    /// </summary>
    [JsonPropertyName("browsePath")]
    public string BrowsePath { get; set; } = string.Empty;

    /// <summary>
    /// Sampling interval in milliseconds. The server samples the value at this rate.
    /// Default: 100ms for responsive industrial monitoring.
    /// </summary>
    [JsonPropertyName("samplingInterval")]
    public int SamplingIntervalMs { get; set; } = 100;

    /// <summary>
    /// Publishing interval in milliseconds. The server sends notifications at this rate.
    /// Default: 500ms for a balance between responsiveness and network efficiency.
    /// </summary>
    [JsonPropertyName("publishingInterval")]
    public int PublishingIntervalMs { get; set; } = 500;

    /// <summary>
    /// Whether this subscription is enabled. Disabled subscriptions are not created on the server.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Queue size for buffering values on the server before they are published.
    /// Higher values prevent data loss during network delays.
    /// </summary>
    [JsonPropertyName("queueSize")]
    public uint QueueSize { get; set; } = 10;

    /// <summary>
    /// Whether to discard oldest values when the queue is full.
    /// True (default) = discard oldest, False = discard newest.
    /// </summary>
    [JsonPropertyName("discardOldest")]
    public bool DiscardOldest { get; set; } = true;
}
