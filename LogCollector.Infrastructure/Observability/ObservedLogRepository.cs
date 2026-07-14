using LogCollector.Application.Interfaces;
using LogCollector.Core.Domain;
using Microsoft.Extensions.Logging;

namespace LogCollector.Infrastructure.Observability;

/// <summary>
/// Writes to the durable primary repository first, then publishes to best-effort observers.
/// This ordering prevents a Loki outage from blocking SQLite or duplicating its rows on retry.
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
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
