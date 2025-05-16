using LogCollector.Core.Models;

namespace LogCollector.Core.Interfaces;

public interface ILogRepository
{
    Task<int> InsertBatchAsync(
        IReadOnlyList<LogEntry> entries,
        CancellationToken ct = default);
}
