using System.Buffers;

namespace LogCollector.Core.Domain;

/// <summary>
/// Parsed syslog message. Text fields are zero-copy slices of one pooled UTF-8 buffer.
/// The pipeline consumer owns <see cref="BufferOwner"/> and must dispose it once.
/// </summary>
public readonly record struct LogEntry
{
    public required int Priority { get; init; }
    public int Facility => Priority >> 3;
    public SyslogSeverity Severity => (SyslogSeverity)(Priority & 7);
    public required ReadOnlyMemory<byte> Timestamp { get; init; }
    public required ReadOnlyMemory<byte> Hostname { get; init; }
    public required ReadOnlyMemory<byte> Topic { get; init; }
    public required ReadOnlyMemory<byte> Message { get; init; }
    public required DateTimeOffset ReceivedAt { get; init; }
    public IMemoryOwner<byte>? BufferOwner { get; init; }
}
