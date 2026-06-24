using System.Buffers;

namespace LogCollector.Core.Domain;

/// <summary>
/// An immutable syslog record that travels through the <c>Channel&lt;LogEntry&gt;</c> pipeline.
/// </summary>
///
/// <remarks>
/// <para><b>Why a <c>readonly struct</c>?</b> (Jeffrey Richter, CLR via C#)</para>
/// <para>
/// At 10 000 log entries per second, every heap allocation is a future GC pause.
/// A <c>readonly struct</c> is a value type: it lives on the stack in local variables
/// and inline in the Channel's ring-buffer array — it is never placed on the GC heap by itself,
/// so the garbage collector never touches the entry data directly.
/// </para>
///
/// <para><b>Why <see cref="ReadOnlyMemory{T}"/> for text fields instead of <c>string</c>?</b></para>
/// <para>
/// <c>ReadOnlySpan&lt;byte&gt;</c> is a ref struct and cannot be stored in a regular struct.
/// <c>ReadOnlyMemory&lt;byte&gt;</c> is a plain 16-byte struct (object-ref + offset + length)
/// that wraps a slice of a pool-rented buffer — no byte data is copied into the entry.
/// Actual <c>string</c> objects are created only in the persistence layer,
/// immediately before the SQLite INSERT statement is executed.
/// </para>
///
/// <para><b>Buffer ownership (<see cref="RawBuffer"/>):</b></para>
/// <para>
/// The text fields are slices into a <see cref="MemoryPool{T}"/>-rented buffer.
/// <see cref="RawBuffer"/> transfers ownership of that buffer through the Channel.
/// The consumer (the batch-write BackgroundService) must call
/// <c>entry.RawBuffer?.Dispose()</c> after the entry has been written to SQLite,
/// which returns the underlying byte array to the pool without any GC allocation.
/// </para>
/// </remarks>
public readonly struct LogEntry
{
    /// <summary>
    /// Raw syslog PRI byte encoding both facility and severity numerically.
    /// Decode with: <c>facility = Priority / 8</c> and <c>syslogSeverity = Priority % 8</c>.
    /// For human-readable classification, prefer the <see cref="Severity"/> field.
    /// </summary>
    public int Priority { get; init; }

    /// <summary>
    /// RFC 3164 timestamp bytes — always exactly 15 bytes: <c>"Mmm DD HH:MM:SS"</c>.
    /// Single-digit days are space-padded on the left (e.g. <c>"Jun  4 18:00:00"</c>).
    /// No timezone, no year. Convert to <see cref="DateTime"/> in the persistence layer only.
    /// </summary>
    public ReadOnlyMemory<byte> TimestampRaw { get; init; }

    /// <summary>MikroTik device hostname, e.g. <c>"mtk-router"</c>.</summary>
    public ReadOnlyMemory<byte> Hostname { get; init; }

    /// <summary>
    /// MikroTik log topic, e.g. <c>"firewall"</c>, <c>"dhcp"</c>, <c>"system"</c>.
    /// </summary>
    public ReadOnlyMemory<byte> Topic { get; init; }

    /// <summary>
    /// Severity resolved to an enum at parse time — stored so downstream consumers
    /// never re-parse the raw text.
    /// </summary>
    public SyslogSeverity Severity { get; init; }

    /// <summary>Free-form message body.</summary>
    public ReadOnlyMemory<byte> Message { get; init; }

    /// <summary>
    /// UTC wall-clock time at which the UDP datagram arrived at the collector.
    /// Distinct from <see cref="TimestampRaw"/>, which is the device's local time
    /// and has no year or timezone.
    /// </summary>
    public DateTimeOffset ReceivedAt { get; init; }

    /// <summary>
    /// Ownership handle for the pool-rented byte array that backs all
    /// <see cref="ReadOnlyMemory{T}"/> fields above.
    /// <para>
    /// The consumer is responsible for calling <c>RawBuffer?.Dispose()</c> exactly once
    /// after the entry is fully consumed. Disposal returns the underlying array to
    /// <see cref="ArrayPool{T}.Shared"/> — no GC allocation occurs.
    /// </para>
    /// <para><c>null</c> for entries created from static memory (e.g. unit tests).</para>
    /// </summary>
    public IMemoryOwner<byte>? RawBuffer { get; init; }
}
