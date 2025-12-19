using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using OpcUaTrayClient.Core.Channel;
using OpcUaTrayClient.Core.Configuration;
using OpcUaTrayClient.Core.Models;
using OpcUaTrayClient.OpcUa;
using OpcUaTrayClient.Persistence;
using OpcUaTrayClient.Persistence.JsonFallback;
using OpcUaTrayClient.Persistence.MongoDB;
using OpcUaTrayClient.Service;
using Serilog;

// Configure Serilog for file logging
var logPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "OpcUaTrayClient", "Logs", "service-.log");

Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.File(
        logPath,
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        fileSizeLimitBytes: 50 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .WriteTo.Console()
    .CreateLogger();

try
{
    Log.Information("OPC UA Service starting...");

    var builder = Host.CreateApplicationBuilder(args);

    // Use Serilog
    builder.Services.AddLogging(loggingBuilder =>
    {
        loggingBuilder.ClearProviders();
        loggingBuilder.AddSerilog(Log.Logger, dispose: true);
    });

    // Configure as Windows Service
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "OpcUaTrayClient";
    });

    // Register services (same as WinForms but without UI dependencies)
    ConfigureServices(builder.Services);

    // Register the worker service
    builder.Services.AddHostedService<OpcUaWorkerService>();

    var host = builder.Build();
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "OPC UA Service terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>
/// Configures all application services (shared with WinForms).
/// </summary>
static void ConfigureServices(IServiceCollection services)
{
    // Create temporary provider for logger
    var tempProvider = services.BuildServiceProvider();
    var configLogger = tempProvider.GetRequiredService<ILogger<ConfigurationService>>();

    // Configuration service (loads from %AppData%)
    var configService = new ConfigurationService(configLogger);
    configService.LoadAsync().GetAwaiter().GetResult();
    var config = configService.Current;

    services.AddSingleton(configService);

    // Channel configuration
    services.AddSingleton(new ChannelConfiguration
    {
        Capacity = config.ChannelCapacity,
        SingleReader = true,
        SingleWriter = false
    });

    // Data point channel
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

    // JSON fallback sink
    services.AddSingleton<JsonFileDataPointSink>(sp =>
    {
        var cfg = sp.GetRequiredService<ConfigurationService>();
        var logger = sp.GetRequiredService<ILogger<JsonFileDataPointSink>>();
        return new JsonFileDataPointSink(
            () => cfg.DataFolderPath,
            () => cfg.ArchiveFolderPath,
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

    // Data persistence service
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

    // OPC UA client manager
    services.AddSingleton<OpcUaClientManager>(sp =>
    {
        var cfg = sp.GetRequiredService<ConfigurationService>().Current;
        return new OpcUaClientManager(
            sp.GetRequiredService<DataPointChannel>(),
            sp.GetRequiredService<ILoggerFactory>(),
            cfg.SessionTimeoutMs,
            cfg.KeepAliveIntervalMs);
    });
}
