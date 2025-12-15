namespace OpcUaTrayClient.Core.Channel;

/// <summary>
/// Configuration for the bounded data point channel.
/// </summary>
public sealed class ChannelConfiguration
{
    /// <summary>
    /// Maximum capacity of the channel.
    /// When full, oldest items are dropped to prevent blocking OPC UA acquisition.
    /// Default: 10,000 items (~60 seconds of data at 150 points/second).
    /// </summary>
    public int Capacity { get; set; } = 10_000;

    /// <summary>
    /// Whether to enable single reader optimization.
    /// Set to true when only one consumer task reads from the channel.
    /// </summary>
    public bool SingleReader { get; set; } = true;

    /// <summary>
    /// Whether to enable single writer optimization.
    /// Set to false when multiple OPC UA subscriptions may write concurrently.
    /// </summary>
    public bool SingleWriter { get; set; } = false;
}
