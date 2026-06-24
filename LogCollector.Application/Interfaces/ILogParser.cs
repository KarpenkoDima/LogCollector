using LogCollector.Core.Domain;

namespace LogCollector.Application.Interfaces;

/// <summary>
/// Attempts to parse a raw datagram into a <see cref="LogEntry"/>.
///
/// <para>
/// Each implementation handles one log format (MikroTik syslog, WinBeat, etc.).
/// <c>UdpSyslogListener</c> depends on this interface — it never references a
/// concrete parser class and is therefore unaffected when formats are added or removed.
/// </para>
///
/// <para>
/// The method must be thread-safe: a single instance is shared across all
/// receive calls on the listener's background thread.
/// </para>
/// </summary>
public interface ILogParser
{
    /// <summary>
    /// Attempts to parse <paramref name="source"/> into a <see cref="LogEntry"/>.
    /// </summary>
    /// <param name="source">
    /// A view into the pool-rented buffer that holds the raw datagram bytes.
    /// The buffer must remain valid for the lifetime of the returned entry.
    /// </param>
    /// <param name="receivedAt">Collector wall-clock time at datagram arrival (UTC).</param>
    /// <param name="entry">The populated entry on success; <c>default</c> on failure.</param>
    /// <returns><c>true</c> if the datagram matched this format; <c>false</c> otherwise.</returns>
    bool TryParse(ReadOnlyMemory<byte> source, DateTimeOffset receivedAt, out LogEntry entry);
}
