using LogCollector.Application.Interfaces;
using LogCollector.Core.Domain;
using Microsoft.Extensions.Logging;

namespace LogCollector.Infrastructure.Observability;

/// <summary>
/// Writes to the durable primary repository first, then publishes to best-effort observers.
/// Observer failures never turn an already committed primary write into a failed batch.
/// </summary>
public sealed partial class ObservedLogRepository : ILogRepository
{
    private readonly ILogRepository _primary;
    private readonly IReadOnlyList<ILogObserver> _observers;
    private readonly ILogger<ObservedLogRepository> _logger;

    public ObservedLogRepository(
        ILogRepository primary,
        IEnumerable<ILogObserver> observers,
        ILogger<ObservedLogRepository> logger)
    {
        _primary = primary;
        _observers = observers.ToArray();
        _logger = logger;
    }

    public Task InitializeAsync(CancellationToken cancellationToken) =>
        _primary.InitializeAsync(cancellationToken);

    public async Task SaveBatchAsync(
        IReadOnlyList<LogEntry> entries,
        CancellationToken cancellationToken)
    {
        await _primary.SaveBatchAsync(entries, cancellationToken).ConfigureAwait(false);

        foreach (ILogObserver observer in _observers)
        {
            try
            {
                await observer.PublishBatchAsync(entries, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // The primary write has already committed. Cancellation of a best-effort
                // observer must not make the batch look uncommitted to BatchProcessor,
                // otherwise a shutdown retry could insert the same SQLite rows twice.
                LogObserverFailure(_logger, observer.GetType().Name, exception);
            }
        }
    }

    public ValueTask DisposeAsync() => _primary.DisposeAsync();

    [LoggerMessage(EventId = 200, Level = LogLevel.Warning,
        Message = "Log observer {Observer} failed; the batch remains safely stored in SQLite")]
    private static partial void LogObserverFailure(
        ILogger logger,
        string observer,
        Exception exception);
}
