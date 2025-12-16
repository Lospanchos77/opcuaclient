using System.Text.Json.Serialization;

namespace OpcUaTrayClient.Core.Models;

/// <summary>
/// Configuration for a single OPC UA server connection.
/// Each server has its own endpoint, subscriptions, and optional timeout overrides.
/// </summary>
public sealed class OpcUaServerConfiguration
{
    /// <summary>
    /// Unique identifier for this server configuration.
    /// Used to correlate data points with their source server.
    /// Auto-generated if not specified.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Human-readable name for this server (e.g., "Production PLC", "Test Server").
    /// Displayed in the UI and stored with data points for identification.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// OPC UA server endpoint URL.
    /// Example: opc.tcp://192.168.1.100:4840/ServerName
    /// </summary>
    [JsonPropertyName("endpointUrl")]
    public string EndpointUrl { get; set; } = string.Empty;

    /// <summary>
    /// Whether this server connection is enabled.
    /// Disabled servers are not connected during acquisition.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Optional session timeout override in milliseconds.
    /// If null, uses the global SessionTimeoutMs from AppConfiguration.
    /// </summary>
    [JsonPropertyName("sessionTimeoutMs")]
    public int? SessionTimeoutMs { get; set; }

    /// <summary>
    /// Optional keepalive interval override in milliseconds.
    /// If null, uses the global KeepAliveIntervalMs from AppConfiguration.
    /// </summary>
    [JsonPropertyName("keepAliveIntervalMs")]
    public int? KeepAliveIntervalMs { get; set; }

    /// <summary>
    /// List of OPC UA node subscriptions specific to this server.
    /// Each server has its own independent set of subscriptions.
    /// </summary>
    [JsonPropertyName("subscriptions")]
    public List<SubscriptionDefinition> Subscriptions { get; set; } = new();

    /// <summary>
    /// Returns a display string for this server configuration.
    /// </summary>
    public override string ToString() =>
        string.IsNullOrEmpty(Name) ? EndpointUrl : $"{Name} ({EndpointUrl})";
}
