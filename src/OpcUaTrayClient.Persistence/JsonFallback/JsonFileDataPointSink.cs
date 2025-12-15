using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpcUaTrayClient.Core.Models;

namespace OpcUaTrayClient.Persistence.JsonFallback;

/// <summary>
/// JSON file-based fallback implementation of IDataPointSink.
///
/// Activated when MongoDB is unavailable. Features:
/// - Writes to configurable folder (default: %AppData%/OpcUaTrayClient/Data/)
/// - One file per day using JSON Lines format (one JSON object per line)
/// - Files can be recovered to MongoDB when it becomes available
/// - Archive folder for processed files (never deleted)
///
/// File format: data_YYYYMMDD.jsonl (JSON Lines - one record per line)
/// This makes it easy to append and read large files efficiently.
///
/// Local filesystem is assumed always available.
/// </summary>
public sealed class JsonFileDataPointSink : IDataPointSink
{
    private readonly ILogger<JsonFileDataPointSink> _logger;
    private readonly Func<string> _getDataFolder;
    private readonly Func<string> _getArchiveFolder;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public string Name => "JSON File";

    // Local filesystem is always available
    public bool IsAvailable => true;

    /// <summary>
    /// Current data folder path.
    /// </summary>
    public string DataFolder => _getDataFolder();

    /// <summary>
    /// Current archive folder path.
    /// </summary>
    public string ArchiveFolder => _getArchiveFolder();

    public JsonFileDataPointSink(
        Func<string> getDataFolder,
        Func<string> getArchiveFolder,
        ILogger<JsonFileDataPointSink> logger)
    {
        _logger = logger;
        _getDataFolder = getDataFolder;
        _getArchiveFolder = getArchiveFolder;

        // Ensure directories exist
        EnsureDirectoriesExist();

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false, // Compact for disk space
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };

        _logger.LogInformation("JsonFileDataPointSink initialized. DataFolder: {DataFolder}", DataFolder);
    }

    private void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(_getDataFolder());
        Directory.CreateDirectory(_getArchiveFolder());
    }

    /// <summary>
    /// Gets the current day's file path.
    /// </summary>
    private string GetCurrentDayFilePath()
    {
        var dataFolder = _getDataFolder();
        var date = DateTime.UtcNow.ToString("yyyyMMdd");
        return Path.Combine(dataFolder, $"data_{date}.jsonl");
    }

    public async Task<bool> WriteAsync(IReadOnlyList<OpcUaDataPoint> dataPoints, CancellationToken ct)
    {
        if (dataPoints.Count == 0) return true;

        // Get current data folder (can change at runtime)
        var dataFolder = _getDataFolder();

        // Ensure directory exists (user may have changed the path)
        Directory.CreateDirectory(dataFolder);

        // Use daily file with JSON Lines format
        var filePath = GetCurrentDayFilePath();

        try
        {
            // Convert each data point to a JSON line
            var lines = new StringBuilder();
            foreach (var dp in dataPoints)
            {
                var json = JsonSerializer.Serialize(dp, _jsonOptions);
                lines.AppendLine(json);
            }

            // Use semaphore to prevent concurrent writes
            await _writeLock.WaitAsync(ct);
            try
            {
                // Append to file (creates if doesn't exist)
                await File.AppendAllTextAsync(filePath, lines.ToString(), ct);
            }
            finally
            {
                _writeLock.Release();
            }

            _logger.LogDebug("JSON fallback: Appended {Count} data points to {File}", dataPoints.Count, Path.GetFileName(filePath));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write JSON fallback file {Path}", filePath);
            return false;
        }
    }

    public Task<bool> HealthCheckAsync(CancellationToken ct)
    {
        try
        {
            var dataFolder = _getDataFolder();
            Directory.CreateDirectory(dataFolder);

            // Simple write test
            var testPath = Path.Combine(dataFolder, ".healthcheck");
            File.WriteAllText(testPath, DateTime.UtcNow.ToString("O"));
            File.Delete(testPath);
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Gets all pending (unprocessed) JSON files in chronological order.
    /// </summary>
    public IEnumerable<string> GetPendingFiles()
    {
        try
        {
            var dataFolder = _getDataFolder();
            if (!Directory.Exists(dataFolder)) return Enumerable.Empty<string>();

            return Directory.GetFiles(dataFolder, "data_*.jsonl")
                .OrderBy(f => f); // Filename includes date, so alphabetical = chronological
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list pending JSON files");
            return Enumerable.Empty<string>();
        }
    }

    /// <summary>
    /// Reads data points from a JSON Lines fallback file.
    /// Each line is a separate JSON object.
    /// </summary>
    public async Task<List<OpcUaDataPoint>> ReadFileAsync(string filePath, CancellationToken ct = default)
    {
        var dataPoints = new List<OpcUaDataPoint>();
        try
        {
            var lines = await File.ReadAllLinesAsync(filePath, ct);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var dp = JsonSerializer.Deserialize<OpcUaDataPoint>(line, _jsonOptions);
                    if (dp != null)
                    {
                        dataPoints.Add(dp);
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to parse JSON line in {Path}: {Line}", filePath, line.Length > 100 ? line[..100] + "..." : line);
                }
            }
            return dataPoints;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read JSON Lines file {Path}", filePath);
            return dataPoints;
        }
    }

    /// <summary>
    /// Moves a processed file to the archive folder.
    /// Files are NEVER deleted - they are archived for audit/recovery purposes.
    /// </summary>
    public void ArchiveFile(string filePath)
    {
        try
        {
            var archiveFolder = _getArchiveFolder();
            Directory.CreateDirectory(archiveFolder);

            var filename = Path.GetFileName(filePath);
            var archivePath = Path.Combine(archiveFolder, filename);

            // Add suffix if file already exists in archive
            if (File.Exists(archivePath))
            {
                var suffix = DateTime.UtcNow.ToString("_HHmmss");
                archivePath = Path.Combine(archiveFolder,
                    Path.GetFileNameWithoutExtension(filename) + suffix + ".jsonl");
            }

            File.Move(filePath, archivePath);
            _logger.LogDebug("Archived JSON file: {From} -> {To}", filePath, archivePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to archive JSON file {Path}", filePath);
        }
    }

    /// <summary>
    /// Gets the count of pending files.
    /// </summary>
    public int GetPendingFileCount()
    {
        try
        {
            var dataFolder = _getDataFolder();
            if (!Directory.Exists(dataFolder)) return 0;
            return Directory.GetFiles(dataFolder, "data_*.jsonl").Length;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Gets the total size of pending files in bytes.
    /// </summary>
    public long GetPendingFilesSize()
    {
        try
        {
            var dataFolder = _getDataFolder();
            if (!Directory.Exists(dataFolder)) return 0;
            return Directory.GetFiles(dataFolder, "data_*.jsonl")
                .Select(f => new FileInfo(f).Length)
                .Sum();
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Cleans up old archive files beyond retention limit.
    /// </summary>
    public void CleanupArchive(int maxAgeDays = 30)
    {
        try
        {
            var archiveFolder = _getArchiveFolder();
            if (!Directory.Exists(archiveFolder)) return;

            var cutoff = DateTime.UtcNow.AddDays(-maxAgeDays);
            var oldFiles = Directory.GetFiles(archiveFolder, "data_*.jsonl")
                .Select(f => new FileInfo(f))
                .Where(f => f.CreationTimeUtc < cutoff)
                .ToList();

            foreach (var file in oldFiles)
            {
                try
                {
                    file.Delete();
                    _logger.LogDebug("Deleted old archive file: {File}", file.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete archive file: {File}", file.Name);
                }
            }

            if (oldFiles.Count > 0)
            {
                _logger.LogInformation("Cleaned up {Count} old archive files", oldFiles.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup archive folder");
        }
    }
}
