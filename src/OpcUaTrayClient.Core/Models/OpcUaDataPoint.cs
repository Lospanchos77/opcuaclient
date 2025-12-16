using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace OpcUaTrayClient.Core.Models;

/// <summary>
/// Represents a single data point acquired from an OPC UA server.
/// This model is used for both MongoDB persistence and JSON fallback storage.
/// </summary>
public sealed class OpcUaDataPoint
{
    /// <summary>
    /// MongoDB document ID. Auto-generated if not set.
    /// </summary>
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    [BsonIgnoreIfDefault]
    public string? Id { get; set; }

    /// <summary>
    /// Unique identifier of the OPC UA server that generated this data point.
    /// Corresponds to OpcUaServerConfiguration.Id.
    /// </summary>
    [BsonElement("serverId")]
    public string ServerId { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable name of the OPC UA server that generated this data point.
    /// Corresponds to OpcUaServerConfiguration.Name.
    /// </summary>
    [BsonElement("serverName")]
    public string ServerName { get; set; } = string.Empty;

    /// <summary>
    /// UTC timestamp when the data point was received by the client.
    /// </summary>
    [BsonElement("timestampUtc")]
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// OPC UA NodeId as string (e.g., "ns=2;s=Demo.Dynamic.Scalar.Int32").
    /// </summary>
    [BsonElement("nodeId")]
    public string NodeId { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable display name of the node.
    /// </summary>
    [BsonElement("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Full hierarchical browse path (e.g., "Simulation/Counter/ValueSimulation/Min Value").
    /// Provides context for data visualization in dashboards.
    /// </summary>
    [BsonElement("browsePath")]
    public string BrowsePath { get; set; } = string.Empty;

    /// <summary>
    /// OPC UA data type name (e.g., "Int32", "Double", "String").
    /// </summary>
    [BsonElement("dataType")]
    public string DataType { get; set; } = string.Empty;

    /// <summary>
    /// The actual value. Stored as object to support any OPC UA data type.
    /// For complex types, this may be serialized as JSON or BSON.
    /// </summary>
    [BsonElement("value")]
    public object? Value { get; set; }

    /// <summary>
    /// OPC UA status code (0 = Good).
    /// </summary>
    [BsonElement("statusCode")]
    public uint StatusCode { get; set; }

    /// <summary>
    /// Human-readable status description (e.g., "Good", "Bad", "Uncertain").
    /// </summary>
    [BsonElement("quality")]
    public string Quality { get; set; } = "Good";

    /// <summary>
    /// Timestamp when the value was generated at the source (sensor/PLC).
    /// </summary>
    [BsonElement("sourceTimestamp")]
    [BsonIgnoreIfNull]
    public DateTime? SourceTimestamp { get; set; }

    /// <summary>
    /// Timestamp when the OPC UA server received/processed the value.
    /// </summary>
    [BsonElement("serverTimestamp")]
    [BsonIgnoreIfNull]
    public DateTime? ServerTimestamp { get; set; }
}
