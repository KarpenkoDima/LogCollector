using LogCollector.Core.Domain;

namespace LogCollector.Application.Interfaces;

/// <summary>
/// A single log destination — SQLite, Loki, ClickHouse, Console, etc.
///
/// <para>
/// <see cref="ILogSink"/> is to <see cref="ILogRepository"/> what <c>ISink</c> is
/// to <c>ILogger</c> in Serilog: the interface for an individual output target.
/// <c>FanOutLogRepository</c> implements <see cref="ILogRepository"/> by dispatching
/// each batch to all registered <see cref="ILogSink"/> instances in parallel.
/// </para>
///
/// <para>
/// Adding a new destination requires only a new <see cref="ILogSink"/> implementation
/// and its corresponding factory.  <c>BatchWriterService</c>, <c>UdpSyslogListener</c>,
/// and <see cref="ILogRepository"/> are never modified.
/// </para>
/// </summary>
public interface ILogSink
{
    /// <summary>
    /// One-time setup called at service startup.
    /// SQLite uses this to create the schema; Loki uses it to warm up the HTTP client.
    /// Sinks that need no setup can omit this — the default implementation is a no-op.
    /// </summary>
    Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Persists or forwards a batch of log entries.
    /// Called by <c>FanOutLogRepository</c> for every batch drained from the channel.
    /// </summary>
    Task SaveBatchAsync(IReadOnlyList<LogEntry> batch, CancellationToken ct);
}
