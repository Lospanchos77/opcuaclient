namespace OpcUaTrayClient.Core.Models;

/// <summary>
/// Represents a node in the OPC UA address space for browse operations.
/// Used to populate the TreeView in the configuration UI.
/// </summary>
public sealed class OpcUaNode
{
    /// <summary>
    /// OPC UA NodeId as string (e.g., "ns=2;i=1234" or "ns=2;s=Demo.Static").
    /// </summary>
    public string NodeId { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable display name.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// OPC UA browse name (namespace-qualified identifier).
    /// </summary>
    public string BrowseName { get; set; } = string.Empty;

    /// <summary>
    /// Node class: Object, Variable, Method, ObjectType, VariableType, ReferenceType, DataType, View.
    /// </summary>
    public OpcUaNodeClass NodeClass { get; set; } = OpcUaNodeClass.Unknown;

    /// <summary>
    /// Data type for Variable nodes (e.g., "Int32", "Double", "String").
    /// Null for non-Variable nodes.
    /// </summary>
    public string? DataType { get; set; }

    /// <summary>
    /// Whether this node is a Variable (can hold a value).
    /// </summary>
    public bool IsVariable => NodeClass == OpcUaNodeClass.Variable;

    /// <summary>
    /// Whether this node can be subscribed to for data changes.
    /// Only Variables with readable access rights are subscribable.
    /// </summary>
    public bool IsSubscribable { get; set; }

    /// <summary>
    /// Whether this node has been expanded (children loaded) in the UI.
    /// </summary>
    public bool IsExpanded { get; set; }

    /// <summary>
    /// Whether this node potentially has children (for lazy loading).
    /// </summary>
    public bool HasChildren { get; set; }

    /// <summary>
    /// Full hierarchical browse path (e.g., "Simulation/Counter/ValueSimulation/Min Value").
    /// Built during tree navigation to provide context for data visualization.
    /// </summary>
    public string BrowsePath { get; set; } = string.Empty;

    /// <summary>
    /// Child nodes. Populated on demand when the node is expanded.
    /// </summary>
    public List<OpcUaNode> Children { get; set; } = new();

    /// <summary>
    /// Namespace index extracted from the NodeId.
    /// </summary>
    public int NamespaceIndex { get; set; }

    public override string ToString() => $"{DisplayName} ({NodeId})";
}

/// <summary>
/// OPC UA node classes.
/// </summary>
public enum OpcUaNodeClass
{
    Unknown = 0,
    Object = 1,
    Variable = 2,
    Method = 4,
    ObjectType = 8,
    VariableType = 16,
    ReferenceType = 32,
    DataType = 64,
    View = 128
}
