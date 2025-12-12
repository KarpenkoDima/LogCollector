using System.Globalization;
using System.Text;
using LogCollector.Application.Interfaces;
using LogCollector.Core.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LogCollector.Infrastructure.Sinks;

// ── LokiSinkFactory ───────────────────────────────────────────────────────────

/// <summary>
/// Creates a <see cref="LokiLogSink"/> from a config section.
/// </summary>
/// <example>
/// appsettings.json:
/// <code>
/// "LogSinks": [
///   {
///     "Type": "Loki",
///     "Endpoint": "http://loki:3100",
///     "Labels": {
///       "app": "logcollector",
///       "env": "production"
///     }
///   }
/// ]
/// </code>
/// </example>
public sealed class LokiSinkFactory : ISinkFactory
{
    public string SinkType => "Loki";

    public ILogSink Create(IConfigurationSection config, IServiceProvider services)
    {
        var endpoint = config["Endpoint"]
            ?? throw new InvalidOperationException(
                "Loki sink requires an 'Endpoint' key (e.g. \"http://loki:3100\").");

        // Read optional static labels: "Labels": { "app": "logcollector", "env": "prod" }
        var labels = config.GetSection("Labels")
            .GetChildren()
            .ToDictionary(c => c.Key, c => c.Value ?? string.Empty);

        return new LokiLogSink(
            endpoint,
            labels,
            services.GetRequiredService<ILogger<LokiLogSink>>());
    }
}

// ── ConsoleSinkFactory ────────────────────────────────────────────────────────

/// <summary>
/// A development sink that writes each entry to stdout in a human-readable format.
/// Useful for verifying the pipeline locally before connecting a real backend.
/// </summary>
/// <example>
/// appsettings.Development.json:
/// <code>
/// "LogSinks": [
///   { "Type": "Console" },
///   { "Type": "Sqlite", "ConnectionString": "Data Source=dev-logs.db" }
/// ]
/// </code>
/// </example>
public sealed class ConsoleSinkFactory : ISinkFactory
{
    public string SinkType => "Console";

    public ILogSink Create(IConfigurationSection config, IServiceProvider services)
        => new ConsoleLogSink();
}

/// <summary>
/// Writes parsed log entries to stdout. For development only.
/// </summary>
internal sealed class ConsoleLogSink : ILogSink
{
    public Task SaveBatchAsync(IReadOnlyList<LogEntry> batch, CancellationToken ct)
    {
        foreach (var e in batch)
        {
            Console.WriteLine(
                "[{0:HH:mm:ss.fff}] {1,-5} {2} {3} — {4}",
                e.ReceivedAt.LocalDateTime,
                e.Severity.ToString().ToUpperInvariant(),
                Encoding.UTF8.GetString(e.Hostname.Span),
                Encoding.UTF8.GetString(e.Topic.Span),
                Encoding.UTF8.GetString(e.Message.Span));
        }
        return Task.CompletedTask;
    }
}
