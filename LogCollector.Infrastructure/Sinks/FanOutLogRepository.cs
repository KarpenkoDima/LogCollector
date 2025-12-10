using LogCollector.Application.Interfaces;
using LogCollector.Core.Domain;

namespace LogCollector.Infrastructure.Sinks;

/// <summary>
/// Implements <see cref="ILogRepository"/> by dispatching each batch to all
/// registered <see cref="ILogSink"/> instances in parallel.
///
/// <para>
/// This is the only class in the system that knows about multiple destinations.
/// <c>BatchWriterService</c> depends only on <see cref="ILogRepository"/> and
/// is completely unaware that its writes fan out to SQLite, Loki, and wherever else.
/// </para>
///
/// <para><b>Error isolation.</b>
/// If one sink throws, <see cref="SaveBatchAsync"/> re-throws an
/// <see cref="AggregateException"/> containing all sink errors.  The caller
/// (<c>BatchWriterService.SaveAndDisposeAsync</c>) logs the error and drops
/// the batch — it does not retry, to avoid duplicates.  Each sink should
/// implement its own retry logic internally if needed.
/// </para>
/// </summary>
public sealed class FanOutLogRepository : ILogRepository
{
    private readonly ILogSink[] _sinks;

    public FanOutLogRepository(ILogSink[] sinks)
    {
        if (sinks.Length == 0)
            throw new ArgumentException("At least one sink is required.", nameof(sinks));
        _sinks = sinks;
    }

    /// <summary>
    /// Calls <see cref="ILogSink.InitializeAsync"/> on all sinks in parallel.
    /// All sinks must initialise successfully before the service accepts traffic.
    /// </summary>
    public Task InitializeAsync(CancellationToken ct)
        => Task.WhenAll(_sinks.Select(s => s.InitializeAsync(ct)));

    /// <summary>
    /// Dispatches <paramref name="batch"/> to every sink concurrently.
    /// Waits for all sinks to complete (or fail) before returning.
    /// </summary>
    public Task SaveBatchAsync(IReadOnlyList<LogEntry> batch, CancellationToken ct)
        => Task.WhenAll(_sinks.Select(s => s.SaveBatchAsync(batch, ct)));
}
