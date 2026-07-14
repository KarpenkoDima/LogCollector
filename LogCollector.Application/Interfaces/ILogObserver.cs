using LogCollector.Core.Domain;

namespace LogCollector.Application.Interfaces;

/// <summary>
/// Receives a batch after it has been durably stored by the primary repository.
/// Observer failures must not cause the primary batch to be inserted again.
/// </summary>
public interface ILogObserver
{
    Task PublishBatchAsync(IReadOnlyList<LogEntry> entries, CancellationToken cancellationToken);
}
