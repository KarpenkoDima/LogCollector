using LogCollector.Core.Models;
using System.Net;

namespace LogCollector.Core.Interfaces;

public interface ILogParser
{
    bool TryParse(ReadOnlyMemory<byte> data, IPEndPoint remoteEndpoint, out LogEntry logEntry);
}
