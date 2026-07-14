using LogCollector.Core.Domain;

namespace LogCollector.Application.Interfaces;

public interface ILogRepository : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task SaveBatchAsync(IReadOnlyList<LogEntry> entries, CancellationToken cancellationToken);
}
