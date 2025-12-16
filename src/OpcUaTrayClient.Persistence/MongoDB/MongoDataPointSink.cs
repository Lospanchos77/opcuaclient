using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using OpcUaTrayClient.Core.Models;

namespace OpcUaTrayClient.Persistence.MongoDB;

/// <summary>
/// MongoDB implementation of IDataPointSink.
///
/// Features:
/// - Batch inserts with InsertManyAsync
/// - Circuit breaker integration for fast-fail
/// - Automatic index creation (nodeId + timestamp, TTL)
/// - Configurable timeout protection
///
/// This is the PRIMARY storage sink. Falls back to JSON when unavailable.
/// </summary>
public sealed class MongoDataPointSink : IDataPointSink
{
    private readonly ILogger<MongoDataPointSink> _logger;
    private readonly IMongoCollection<BsonDocument> _collection;
    private readonly CircuitBreakerState _circuitBreaker;
    private readonly TimeSpan _writeTimeout;
    private bool _indexesCreated;

    public string Name => "MongoDB";

    public bool IsAvailable => _circuitBreaker.AllowRequest();

    public MongoDataPointSink(
        IMongoDatabase database,
        string collectionName,
        CircuitBreakerState circuitBreaker,
        ILogger<MongoDataPointSink> logger,
        int writeTimeoutSeconds = 5,
        int ttlDays = 30)
    {
        _logger = logger;
        _circuitBreaker = circuitBreaker;
        _writeTimeout = TimeSpan.FromSeconds(writeTimeoutSeconds);
        _collection = database.GetCollection<BsonDocument>(collectionName);

        // Create indexes in background (don't block startup)
        _ = CreateIndexesAsync(ttlDays);
    }

    private async Task CreateIndexesAsync(int ttlDays)
    {
        if (_indexesCreated) return;

        try
        {
            var indexes = new List<CreateIndexModel<BsonDocument>>
            {
                // Compound index for querying by node and time
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys
                        .Ascending("nodeId")
                        .Descending("sourceTimestamp"),
                    new CreateIndexOptions { Background = true, Name = "idx_nodeId_sourceTimestamp" }),

                // Index for time-based queries
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Descending("timestampUtc"),
                    new CreateIndexOptions { Background = true, Name = "idx_timestampUtc" }),

                // V2.0.0: Compound index for server + node + time queries
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys
                        .Ascending("serverId")
                        .Ascending("nodeId")
                        .Descending("sourceTimestamp"),
                    new CreateIndexOptions { Background = true, Name = "idx_serverId_nodeId_sourceTimestamp" }),

                // V2.0.0: Index for server + time queries
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys
                        .Ascending("serverId")
                        .Descending("timestampUtc"),
                    new CreateIndexOptions { Background = true, Name = "idx_serverId_timestampUtc" })
            };

            // TTL index for automatic data cleanup (if enabled)
            if (ttlDays > 0)
            {
                indexes.Add(new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("timestampUtc"),
                    new CreateIndexOptions
                    {
                        Background = true,
                        Name = "idx_ttl",
                        ExpireAfter = TimeSpan.FromDays(ttlDays)
                    }));
            }

            await _collection.Indexes.CreateManyAsync(indexes);
            _indexesCreated = true;
            _logger.LogInformation("MongoDB indexes created. TTL: {TTL} days", ttlDays);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create MongoDB indexes. Will retry on next write.");
        }
    }

    public async Task<bool> WriteAsync(IReadOnlyList<OpcUaDataPoint> dataPoints, CancellationToken ct)
    {
        if (dataPoints.Count == 0) return true;

        // Fast-fail if circuit breaker is open
        if (!_circuitBreaker.AllowRequest())
        {
            _logger.LogDebug("MongoDB write skipped: circuit breaker open");
            return false;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_writeTimeout);

            var documents = dataPoints.Select(ToBsonDocument).ToList();

            // Use IsOrdered = false for parallel inserts (better performance)
            await _collection.InsertManyAsync(
                documents,
                new InsertManyOptions { IsOrdered = false },
                cts.Token);

            _circuitBreaker.RecordSuccess();
            _logger.LogDebug("MongoDB: Inserted {Count} data points", dataPoints.Count);
            return true;
        }
        catch (MongoBulkWriteException<BsonDocument> ex)
        {
            // Partial success - some documents were inserted
            var insertedCount = ex.Result?.InsertedCount ?? 0;
            _logger.LogWarning(ex, "MongoDB bulk write partial failure: {Inserted}/{Total} inserted",
                insertedCount, dataPoints.Count);

            if (insertedCount > 0)
            {
                // Partial success counts as success for circuit breaker
                _circuitBreaker.RecordSuccess();
                return true;
            }

            _circuitBreaker.RecordFailure();
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Write timeout
            _logger.LogWarning("MongoDB write timed out ({Timeout}ms) for {Count} data points",
                _writeTimeout.TotalMilliseconds, dataPoints.Count);
            _circuitBreaker.RecordFailure();
            return false;
        }
        catch (MongoException ex)
        {
            _logger.LogError(ex, "MongoDB write failed for {Count} data points", dataPoints.Count);
            _circuitBreaker.RecordFailure();
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error writing to MongoDB");
            _circuitBreaker.RecordFailure();
            return false;
        }
    }

    public async Task<bool> HealthCheckAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));

            // Simple ping test
            await _collection.Database.RunCommandAsync<BsonDocument>(
                new BsonDocument("ping", 1),
                cancellationToken: cts.Token);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static BsonDocument ToBsonDocument(OpcUaDataPoint dp)
    {
        var doc = new BsonDocument
        {
            { "serverId", dp.ServerId },       // V2.0.0: Server identification
            { "serverName", dp.ServerName },   // V2.0.0: Server display name
            { "timestampUtc", dp.TimestampUtc },
            { "nodeId", dp.NodeId },
            { "displayName", dp.DisplayName },
            { "browsePath", dp.BrowsePath },
            { "dataType", dp.DataType },
            { "statusCode", (long)dp.StatusCode },
            { "quality", dp.Quality }
        };

        // Handle value - convert to appropriate BSON type
        if (dp.Value != null)
        {
            doc["value"] = BsonValue.Create(ConvertValue(dp.Value));
        }
        else
        {
            doc["value"] = BsonNull.Value;
        }

        if (dp.SourceTimestamp.HasValue)
        {
            doc["sourceTimestamp"] = dp.SourceTimestamp.Value;
        }

        if (dp.ServerTimestamp.HasValue)
        {
            doc["serverTimestamp"] = dp.ServerTimestamp.Value;
        }

        return doc;
    }

    private static object? ConvertValue(object value)
    {
        // Handle OPC UA specific types and arrays
        return value switch
        {
            // Primitive types pass through
            bool or byte or sbyte or short or ushort or int or uint or long or ulong
                or float or double or decimal or string or DateTime => value,

            // Byte array to binary (must be before Array)
            byte[] bytes => bytes,

            // Arrays - convert element by element
            Array arr => arr.Cast<object>().Select(ConvertValue).ToList(),

            // Guid to string
            Guid g => g.ToString(),

            // Default - try to serialize as string
            _ => value.ToString()
        };
    }
}
