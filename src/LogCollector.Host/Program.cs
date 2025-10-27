// ==============================================================================
// LOG COLLECTOR: PROGRAM.CS (Architectural Explanation)
// ==============================================================================

using LogCollector.Application.Channels;
using LogCollector.Application.Options;
using LogCollector.Application.Services;
using LogCollector.Core.Interfaces;
using LogCollector.Infrastructure.Network;
using LogCollector.Infrastructure.Parsers;
using LogCollector.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;

var builder = Host.CreateApplicationBuilder(args);

// ------------------------------------------------------------------------------
// 1. LOGGING CONFIGURATION
// ------------------------------------------------------------------------------
// In a Dockerized environment, the Docker Daemon automatically captures anything 
// written to standard output (stdout). By clearing default providers (like EventLog), 
// we reduce memory overhead and rely solely on the lightweight Console provider.
builder.Logging
    .ClearProviders()
    .AddConsole();

// ------------------------------------------------------------------------------
// 2. CORE CHANNELS
// ------------------------------------------------------------------------------
// The LogChannel acts as an in-memory thread-safe queue (likely using System.Threading.Channels).
// It must be a Singleton so that Publishers (UDP/TCP Listeners) and Consumers (BatchWriter) 
// are pushing/pulling from the exact same instance in memory.
builder.Services.AddSingleton<LogChannel>();

// ------------------------------------------------------------------------------
// 3. PARSERS (.NET 8 KEYED SERVICES)
// ------------------------------------------------------------------------------
// This is the cleanest way to handle multiple implementations of the exact same interface.
// Instead of creating redundant interfaces (IWinLogbeatParser, ISyslogParser), we tag 
// them with a string key. We can later request specific implementations by this exact key.
builder.Services.AddKeyedSingleton<ILogParser, JsonLogParser>("winlogbeat");
builder.Services.AddKeyedSingleton<ILogParser, SyslogParser>("mikrotik");

// ------------------------------------------------------------------------------
// 4. DATABASE & REPOSITORY
// ------------------------------------------------------------------------------
// 1. Get connection string or fallback
var connectionString = builder.Configuration["Database:ConnectionString"]
    ?? "Data Source=logs_2.db";

// 2. Safely parse the connection string to extract the file name/path
var connectionBuilder = new SqliteConnectionStringBuilder(connectionString);
var dbFileName = connectionBuilder.DataSource; // Extracts just "log.db" or the given path

// 3. Combine with ContentRootPath to guarantee an absolute path
var folderPath = builder.Environment.ContentRootPath;
var absoluteDbPath = Path.Combine(folderPath, dbFileName);

// 4. Check if the directory exists, create if missing
var directory = Path.GetDirectoryName(absoluteDbPath);
if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
{
    Directory.CreateDirectory(directory);
}

// 5. Update the connection string with the resolved absolute path
connectionBuilder.DataSource = absoluteDbPath;
var finalConnectionString = connectionBuilder.ToString();

// 6. Register the repository
// Note: As your comment states, ensure SqliteLogRepository opens and closes 
// connections per-method rather than holding one open, since it is a Singleton.
builder.Services.AddSingleton<ILogRepository>(_ => new SqliteLogRepository(finalConnectionString));

// ------------------------------------------------------------------------------
// 5. CONFIGURATION BINDING (OPTIONS PATTERN)
// ------------------------------------------------------------------------------
// Maps the "BatchWriter" JSON section directly to a strongly-typed C# class.
// This allows injection of IOptions<LogCollectorOptions> into your services.
builder.Services.Configure<LogCollectorOptions>(
    builder.Configuration.GetSection("BatchWriter"));

// ------------------------------------------------------------------------------
// 6. BACKGROUND WORKERS (THE FORWARDING PATTERN)
// ------------------------------------------------------------------------------
// We use a two-step registration for our background services. 
// Step A: Register as a Singleton so the DI container knows exactly how to build it.
builder.Services.AddSingleton<BatchWriteService>();

// Step B: Register the HostedService, but forward the resolution to the Singleton above.
// This guarantees we only ever have ONE instance of the BatchWriteService running,
// preventing accidental duplication if we later inject it into another class.
builder.Services.AddHostedService(sp => sp.GetRequiredService<BatchWriteService>());

// ------------------------------------------------------------------------------
// 7. NETWORK LISTENERS (EXPLICIT FACTORY INSTANTIATION)
// ------------------------------------------------------------------------------
// Because our Listeners share the same ILogParser dependency but require *different*
// underlying implementations, we use the Factory pattern (sp => new ...) to manually
// wire them up. This bypasses the DI resolution error we saw earlier.

// TCP Listener (Port 5514) -> Explicitly requests the "winlogbeat" Json Parser
builder.Services.AddSingleton(sp => new TcpLogListener(
    channel: sp.GetRequiredService<LogChannel>(),
    parser: sp.GetRequiredKeyedService<ILogParser>("winlogbeat"), // <--- Resolves Keyed Service
    logger: sp.GetRequiredService<ILogger<TcpLogListener>>(),
    endpoint: new System.Net.IPEndPoint(IPAddress.Any, 5514)));
builder.Services.AddHostedService(sp => sp.GetRequiredService<TcpLogListener>());

// UDP Listener (Port 514) -> Explicitly requests the "mikrotik" Syslog Parser
builder.Services.AddSingleton(sp => new UdpLogListener(
    channel: sp.GetRequiredService<LogChannel>(),
    parser: sp.GetRequiredKeyedService<ILogParser>("mikrotik"),   // <--- Resolves Keyed Service
    logger: sp.GetRequiredService<ILogger<UdpLogListener>>(),
    endpoint: new System.Net.IPEndPoint(IPAddress.Any, 514)));
builder.Services.AddHostedService(sp => sp.GetRequiredService<UdpLogListener>());

// ------------------------------------------------------------------------------
// 8. APPLICATION STARTUP & DATABASE INIT
// ------------------------------------------------------------------------------
var host = builder.Build();

// Before starting the background services, we create a temporary "Scope".
// This allows us to safely resolve services, initialize up the SQLite database 
// (creating tables and WAL-mode pragmas if they don't exist), and then destroy 
// the scope to cleanly free up memory before the main loop begins.
await using (var scope = host.Services.CreateAsyncScope())
{
    var repo = scope.ServiceProvider.GetRequiredService<ILogRepository>();
    if (repo is SqliteLogRepository sqlite)
    {
        await sqlite.InitializeAsync();
    }
}

// Finally, start the host. This triggers StartAsync() on all registered Hosted Services 
// (TCP listener, UDP listener, and BatchWriter) and keeps the application running 
// indefinitely until an exit signal (like CTRL+C or Docker SIGTERM) is received.
await host.RunAsync();