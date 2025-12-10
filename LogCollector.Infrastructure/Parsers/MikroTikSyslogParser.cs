using LogCollector.Application.Interfaces;
using LogCollector.Core.Domain;

namespace LogCollector.Infrastructure.Parsers;

/// <summary>
/// <see cref="ILogParser"/> adapter for the MikroTik RFC 3164 syslog format.
///
/// <para>
/// This class is intentionally thin: all byte-level parsing logic lives in the
/// static <see cref="SyslogParser"/> class (zero-allocation, no heap pressure).
/// This adapter exists solely to make that logic injectable via <see cref="ILogParser"/>,
/// so <c>UdpSyslogListener</c> can be extended with new formats without modification.
/// </para>
/// </summary>
public sealed class MikroTikSyslogParser : ILogParser
{
    /// <inheritdoc/>
    public bool TryParse(ReadOnlyMemory<byte> source, DateTimeOffset receivedAt, out LogEntry entry)
        => SyslogParser.TryParse(source, receivedAt, out entry);
}
