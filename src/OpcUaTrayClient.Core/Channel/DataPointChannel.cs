using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using OpcUaTrayClient.Core.Models;
using SysChannel = System.Threading.Channels.Channel;

namespace OpcUaTrayClient.Core.Channel;

/// <summary>
/// Thread-safe bounded channel for decoupling OPC UA acquisition from persistence.
///
/// CRITICAL DESIGN PRINCIPLE:
/// - TryWrite() is NON-BLOCKING and returns immediately
/// - If the channel is full, the oldest item is dropped (not the new one)
/// - OPC UA acquisition NEVER waits for persistence
///
/// This ensures that even if MongoDB is slow or unavailable, data acquisition continues.
/// </summary>
public sealed class DataPointChannel : IDisposable
{
    private readonly Channel<OpcUaDataPoint> _channel;
    private readonly ILogger<DataPointChannel> _logger;
    private int _droppedCount;
    private int _totalWritten;
    private bool _disposed;

    public DataPointChannel(ChannelConfiguration configuration, ILogger<DataPointChannel> logger)
    {
        _logger = logger;

        // Create bounded channel with DropOldest policy
        // This is the KEY to decoupling: OPC UA never blocks
        _channel = SysChannel.CreateBounded<OpcUaDataPoint>(new BoundedChannelOptions(configuration.Capacity)
        {
            // CRITICAL: Drop oldest items when full - never block the writer
            FullMode = BoundedChannelFullMode.DropOldest,

            // Optimization: single consumer task reads from the channel
            SingleReader = configuration.SingleReader,

            // Multiple OPC UA subscriptions may write concurrently
            SingleWriter = configuration.SingleWriter,

            // Prevent synchronous continuations that could block OPC UA thread
            AllowSynchronousContinuations = false
        });

        _logger.LogInformation("DataPointChannel created with capacity {Capacity}", configuration.Capacity);
    }

    /// <summary>
    /// Non-blocking write. Returns immediately.
    /// If the channel is full, the oldest item is automatically dropped.
    /// </summary>
    /// <param name="dataPoint">The data point to enqueue.</param>
    /// <returns>True if written successfully, false if an error occurred.</returns>
    public bool TryWrite(OpcUaDataPoint dataPoint)
    {
        if (_disposed)
        {
            _logger.LogWarning("Attempted to write to disposed channel");
            return false;
        }

        // This never blocks - if full, oldest item is dropped automatically
        if (_channel.Writer.TryWrite(dataPoint))
        {
            Interlocked.Increment(ref _totalWritten);
            return true;
        }

        // TryWrite can fail if channel is completed, not just full
        // Since we use DropOldest, this means the channel was closed
        _logger.LogWarning("Channel write failed - channel may be completed");
        return false;
    }

    /// <summary>
    /// Notify the channel that an item was dropped due to capacity limits.
    /// Called by the channel's DropOldest mechanism (via callback if we could hook it).
    /// Since System.Threading.Channels doesn't provide a drop callback, we estimate drops.
    /// </summary>
    public void NotifyDropped()
    {
        var count = Interlocked.Increment(ref _droppedCount);

        // Log every 100 drops to avoid log flooding
        if (count % 100 == 0)
        {
            _logger.LogWarning("Channel capacity exceeded. Total dropped: {DroppedCount}", count);
        }
    }

    /// <summary>
    /// Gets the channel reader for the consumer task.
    /// </summary>
    public ChannelReader<OpcUaDataPoint> Reader => _channel.Reader;

    /// <summary>
    /// Gets the channel writer (for testing/advanced scenarios).
    /// Prefer using TryWrite() instead.
    /// </summary>
    public ChannelWriter<OpcUaDataPoint> Writer => _channel.Writer;

    /// <summary>
    /// Current number of items waiting in the channel.
    /// </summary>
    public int CurrentCount => _channel.Reader.Count;

    /// <summary>
    /// Total number of items dropped due to capacity limits.
    /// Note: This is approximate since we can't hook into DropOldest directly.
    /// </summary>
    public int DroppedCount => _droppedCount;

    /// <summary>
    /// Total number of items written to the channel.
    /// </summary>
    public int TotalWritten => _totalWritten;

    /// <summary>
    /// Signals that no more items will be written.
    /// Call this during shutdown to allow the consumer to drain and complete.
    /// </summary>
    public void Complete()
    {
        _channel.Writer.TryComplete();
        _logger.LogInformation("DataPointChannel completed. Total written: {TotalWritten}, Dropped: {DroppedCount}",
            _totalWritten, _droppedCount);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Complete();
    }
}
