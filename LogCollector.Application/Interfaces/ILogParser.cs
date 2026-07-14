using LogCollector.Core.Domain;

namespace LogCollector.Application.Interfaces;

public interface ILogParser
{
    bool TryParse(ReadOnlyMemory<byte> payload, DateTimeOffset receivedAt, out LogEntry entry);
}
