using System.ServiceProcess;
using Microsoft.Extensions.Logging;

namespace OpcUaTrayClient.WinForms.Services;

/// <summary>
/// Helper class to control Windows Services.
/// Used when UseWindowsService configuration option is enabled.
/// </summary>
public class WindowsServiceHelper : IDisposable
{
    private readonly ILogger<WindowsServiceHelper> _logger;
    private readonly string _serviceName;
    private ServiceController? _serviceController;

    public WindowsServiceHelper(ILogger<WindowsServiceHelper> logger, string serviceName)
    {
        _logger = logger;
        _serviceName = serviceName;
        RefreshServiceController();
    }

    /// <summary>
    /// Gets the current status of the service.
    /// </summary>
    public ServiceControllerStatus? Status
    {
        get
        {
            try
            {
                RefreshServiceController();
                _serviceController?.Refresh();
                return _serviceController?.Status;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not get service status");
                return null;
            }
        }
    }

    /// <summary>
    /// Gets whether the service is installed.
    /// </summary>
    public bool IsInstalled
    {
        get
        {
            try
            {
                RefreshServiceController();
                // Accessing Status will throw if service doesn't exist
                _ = _serviceController?.Status;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Gets whether the service is running.
    /// </summary>
    public bool IsRunning => Status == ServiceControllerStatus.Running;

    /// <summary>
    /// Gets whether the service is stopped.
    /// </summary>
    public bool IsStopped => Status == ServiceControllerStatus.Stopped;

    /// <summary>
    /// Gets a human-readable status string.
    /// </summary>
    public string StatusText
    {
        get
        {
            if (!IsInstalled)
                return "Non installé";

            return Status switch
            {
                ServiceControllerStatus.Running => "En cours d'exécution",
                ServiceControllerStatus.Stopped => "Arrêté",
                ServiceControllerStatus.StartPending => "Démarrage...",
                ServiceControllerStatus.StopPending => "Arrêt...",
                ServiceControllerStatus.Paused => "En pause",
                ServiceControllerStatus.PausePending => "Mise en pause...",
                ServiceControllerStatus.ContinuePending => "Reprise...",
                _ => "Inconnu"
            };
        }
    }

    /// <summary>
    /// Starts the service.
    /// </summary>
    public async Task<bool> StartAsync(TimeSpan timeout = default)
    {
        if (timeout == default) timeout = TimeSpan.FromSeconds(30);

        try
        {
            if (!IsInstalled)
            {
                _logger.LogError("Cannot start service '{ServiceName}': not installed", _serviceName);
                return false;
            }

            if (IsRunning)
            {
                _logger.LogInformation("Service '{ServiceName}' is already running", _serviceName);
                return true;
            }

            _logger.LogInformation("Starting service '{ServiceName}'...", _serviceName);
            _serviceController!.Start();

            await Task.Run(() => _serviceController.WaitForStatus(ServiceControllerStatus.Running, timeout));

            _logger.LogInformation("Service '{ServiceName}' started successfully", _serviceName);
            return true;
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            _logger.LogError("Timeout waiting for service '{ServiceName}' to start", _serviceName);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start service '{ServiceName}'", _serviceName);
            return false;
        }
    }

    /// <summary>
    /// Stops the service.
    /// </summary>
    public async Task<bool> StopAsync(TimeSpan timeout = default)
    {
        if (timeout == default) timeout = TimeSpan.FromSeconds(30);

        try
        {
            if (!IsInstalled)
            {
                _logger.LogError("Cannot stop service '{ServiceName}': not installed", _serviceName);
                return false;
            }

            if (IsStopped)
            {
                _logger.LogInformation("Service '{ServiceName}' is already stopped", _serviceName);
                return true;
            }

            _logger.LogInformation("Stopping service '{ServiceName}'...", _serviceName);
            _serviceController!.Stop();

            await Task.Run(() => _serviceController.WaitForStatus(ServiceControllerStatus.Stopped, timeout));

            _logger.LogInformation("Service '{ServiceName}' stopped successfully", _serviceName);
            return true;
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            _logger.LogError("Timeout waiting for service '{ServiceName}' to stop", _serviceName);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop service '{ServiceName}'", _serviceName);
            return false;
        }
    }

    /// <summary>
    /// Restarts the service (stop then start).
    /// </summary>
    public async Task<bool> RestartAsync(TimeSpan timeout = default)
    {
        if (timeout == default) timeout = TimeSpan.FromSeconds(30);

        _logger.LogInformation("Restarting service '{ServiceName}'...", _serviceName);

        if (IsRunning)
        {
            if (!await StopAsync(timeout))
                return false;
        }

        return await StartAsync(timeout);
    }

    /// <summary>
    /// Gets the path to the service log file.
    /// </summary>
    public string GetLogFilePath()
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "OpcUaTrayClient", "Logs");

        // Find the most recent service log file
        if (Directory.Exists(logDir))
        {
            var logFiles = Directory.GetFiles(logDir, "service-*.log")
                .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                .FirstOrDefault();

            if (logFiles != null)
                return logFiles;
        }

        return Path.Combine(logDir, "service-.log");
    }

    /// <summary>
    /// Reads the last N lines from the service log file.
    /// </summary>
    public string[] ReadLogTail(int lines = 100)
    {
        try
        {
            var logPath = GetLogFilePath();
            if (!File.Exists(logPath))
                return new[] { "Fichier de log non trouvé: " + logPath };

            // Read file with shared access (service may be writing to it)
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            var allLines = new List<string>();
            while (!reader.EndOfStream)
            {
                var line = reader.ReadLine();
                if (line != null)
                    allLines.Add(line);
            }

            return allLines.TakeLast(lines).ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read service log");
            return new[] { $"Erreur de lecture du log: {ex.Message}" };
        }
    }

    private void RefreshServiceController()
    {
        try
        {
            _serviceController?.Dispose();
            _serviceController = new ServiceController(_serviceName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create ServiceController for '{ServiceName}'", _serviceName);
            _serviceController = null;
        }
    }

    public void Dispose()
    {
        _serviceController?.Dispose();
    }
}
