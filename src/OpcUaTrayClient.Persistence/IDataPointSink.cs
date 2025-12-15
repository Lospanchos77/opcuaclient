using OpcUaTrayClient.Core.Models;

namespace OpcUaTrayClient.Persistence;

/// <summary>
/// Abstraction for data point storage.
/// Implementations: MongoDataPointSink (primary), JsonFileDataPointSink (fallback).
///
/// The DataPersistenceService uses this interface to transparently switch
/// between MongoDB and JSON storage based on availability.
/// </summary>
public interface IDataPointSink
{
    /// <summary>
    /// Name of the sink for logging and UI display.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Whether the sink is currently available.
    /// For MongoDB: checks circuit breaker and last health check.
    /// For JSON: always true (local filesystem).
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Writes a batch of data points to storage.
    /// </summary>
    /// <param name="dataPoints">The data points to write.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if write succeeded, false otherwise.</returns>
    Task<bool> WriteAsync(IReadOnlyList<OpcUaDataPoint> dataPoints, CancellationToken ct);

    /// <summary>
    /// Performs a health check to verify the sink is operational.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if healthy, false otherwise.</returns>
    Task<bool> HealthCheckAsync(CancellationToken ct);
}
