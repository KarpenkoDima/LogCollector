using LogCollector.Core.Models;
using System.Net;

namespace LogCollector.Core.Interfaces;

public interface ILogParser
{
    LogEntry? TryParse(ReadOnlyMemory<byte> data, IPEndPoint remoteEndpoint);
}
