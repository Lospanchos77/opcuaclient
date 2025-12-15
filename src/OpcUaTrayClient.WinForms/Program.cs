using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using OpcUaTrayClient.Core.Channel;
using OpcUaTrayClient.Core.Configuration;
using OpcUaTrayClient.Core.Models;
using OpcUaTrayClient.OpcUa;
using OpcUaTrayClient.Persistence;
using OpcUaTrayClient.Persistence.JsonFallback;
using OpcUaTrayClient.Persistence.MongoDB;

namespace OpcUaTrayClient.WinForms;

/// <summary>
/// Application entry point.
/// Configures dependency injection and starts the tray application.
/// </summary>
internal static class Program
{
    /// <summary>
    /// The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        // Standard WinForms initialization
        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        // Build the service provider
        var services = ConfigureServices();

        // Run the tray application
        using var trayContext = new TrayApplicationContext(services);
        Application.Run(trayContext);
    }

    /// <summary>
    /// Configures dependency injection services.
    /// </summary>
    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Logging - build a temporary provider to get logger for ConfigurationService
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddConsole();
        });

        // Create and load ConfigurationService BEFORE registering it
        var tempProvider = services.BuildServiceProvider();
        var configLogger = tempProvider.GetRequiredService<ILogger<ConfigurationService>>();
        var configService = new ConfigurationService(configLogger);
        configService.LoadAsync().GetAwaiter().GetResult();
        var config = configService.Current;

        // Register the SAME instance that was loaded
        services.AddSingleton(configService);

        // Channel configuration
        services.AddSingleton(new ChannelConfiguration
        {
            Capacity = config.ChannelCapacity,
            SingleReader = true,
            SingleWriter = false
        });

        // Data point channel (singleton - core decoupling mechanism)
        services.AddSingleton<DataPointChannel>();

        // MongoDB client and database
        services.AddSingleton<IMongoClient>(sp =>
        {
            var cfg = sp.GetRequiredService<ConfigurationService>().Current;
            var settings = MongoClientSettings.FromConnectionString(cfg.MongoConnectionString);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(cfg.MongoTimeoutSeconds);
            settings.ConnectTimeout = TimeSpan.FromSeconds(cfg.MongoTimeoutSeconds);
            settings.SocketTimeout = TimeSpan.FromSeconds(cfg.MongoTimeoutSeconds);
            return new MongoClient(settings);
        });

        services.AddSingleton<IMongoDatabase>(sp =>
        {
            var client = sp.GetRequiredService<IMongoClient>();
            var cfg = sp.GetRequiredService<ConfigurationService>().Current;
            return client.GetDatabase(cfg.MongoDatabaseName);
        });

        // Circuit breaker
        services.AddSingleton<CircuitBreakerState>();

        // MongoDB health monitor
        services.AddSingleton<MongoHealthMonitor>(sp =>
        {
            var cfg = sp.GetRequiredService<ConfigurationService>().Current;
            var logger = sp.GetRequiredService<ILogger<MongoHealthMonitor>>();
            return new MongoHealthMonitor(
                cfg.MongoConnectionString,
                logger,
                cfg.MongoHealthCheckIntervalSeconds,
                cfg.MongoTimeoutSeconds,
                failureThreshold: 3);
        });

        // MongoDB data sink
        services.AddSingleton<MongoDataPointSink>(sp =>
        {
            var db = sp.GetRequiredService<IMongoDatabase>();
            var cfg = sp.GetRequiredService<ConfigurationService>().Current;
            var circuitBreaker = sp.GetRequiredService<CircuitBreakerState>();
            var logger = sp.GetRequiredService<ILogger<MongoDataPointSink>>();
            return new MongoDataPointSink(
                db,
                cfg.MongoCollectionName,
                circuitBreaker,
                logger,
                cfg.MongoTimeoutSeconds,
                cfg.MongoTtlDays);
        });

        // JSON fallback sink (uses dynamic paths from ConfigurationService)
        services.AddSingleton<JsonFileDataPointSink>(sp =>
        {
            var cfg = sp.GetRequiredService<ConfigurationService>();
            var logger = sp.GetRequiredService<ILogger<JsonFileDataPointSink>>();
            return new JsonFileDataPointSink(
                () => cfg.DataFolderPath,    // Lambda pour chemin dynamique
                () => cfg.ArchiveFolderPath, // Lambda pour chemin dynamique
                logger);
        });

        // Recovery service
        services.AddSingleton<RecoveryService>(sp =>
        {
            var cfg = sp.GetRequiredService<ConfigurationService>().Current;
            return new RecoveryService(
                sp.GetRequiredService<JsonFileDataPointSink>(),
                sp.GetRequiredService<MongoDataPointSink>(),
                sp.GetRequiredService<MongoHealthMonitor>(),
                sp.GetRequiredService<ILogger<RecoveryService>>(),
                cfg.MongoBatchSize);
        });

        // Data persistence service (background service)
        services.AddSingleton<DataPersistenceService>(sp =>
        {
            var cfg = sp.GetRequiredService<ConfigurationService>().Current;
            return new DataPersistenceService(
                sp.GetRequiredService<DataPointChannel>(),
                sp.GetRequiredService<MongoDataPointSink>(),
                sp.GetRequiredService<JsonFileDataPointSink>(),
                sp.GetRequiredService<MongoHealthMonitor>(),
                sp.GetRequiredService<RecoveryService>(),
                sp.GetRequiredService<ILogger<DataPersistenceService>>(),
                cfg.MongoBatchSize,
                cfg.MongoBatchTimeoutMs,
                cfg.ForceJsonOnly,
                cfg.DryRunMode);
        });

        // OPC UA client
        services.AddSingleton<OpcUaClientService>(sp =>
        {
            var cfg = sp.GetRequiredService<ConfigurationService>().Current;
            return new OpcUaClientService(
                sp.GetRequiredService<DataPointChannel>(),
                sp.GetRequiredService<ILogger<OpcUaClientService>>(),
                cfg.SessionTimeoutMs,
                cfg.KeepAliveIntervalMs);
        });

        // Build and return the service provider
        return services.BuildServiceProvider();
    }
}
