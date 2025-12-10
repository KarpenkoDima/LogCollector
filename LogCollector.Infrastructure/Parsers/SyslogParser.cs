using System.Buffers.Text;
using LogCollector.Core.Domain;

namespace LogCollector.Infrastructure.Parsers;

/// <summary>
/// Zero-allocation parser for two MikroTik syslog formats:
///
/// Format A — bsd-syslog=no (MikroTik extended, preferred):
///   <30>Jun  4 18:00:00 mtk-router : firewall,info forward: in:ether1...
///
/// Format B — bsd-syslog=yes (RFC 3164 standard):
///   <30>Jun 18 20:50:28 MikroTikHome filter rule changed by admin
///
/// Оба формата поддерживаются одним методом.
/// </summary>
public static class SyslogParser
{
    private const int TimestampByteLength = 15;

    public static bool TryParse(
        ReadOnlyMemory<byte> source,
        DateTimeOffset receivedAt,
        out LogEntry entry)
    {
        entry = default;
        var span = source.Span;

        if (span.IsEmpty || span[0] != (byte)'<')
            return false;

        int cursor = 0;

        // ── 1. PRI ────────────────────────────────────────────────────────────
        int angleClose = span.IndexOf((byte)'>');
        if (angleClose <= 1)
            return false;

        if (!Utf8Parser.TryParse(span[1..angleClose], out int priority, out _))
            return false;

        cursor = angleClose + 1;

        // ── 2. Timestamp — RFC 3164: всегда 15 байт ──────────────────────────
        if (cursor + TimestampByteLength >= span.Length)
            return false;

        var timestampMemory = source.Slice(cursor, TimestampByteLength);
        cursor += TimestampByteLength + 1;

        // ── 3. Hostname — до первого пробела ─────────────────────────────────
        int spaceAfterHost = span[cursor..].IndexOf((byte)' ');
        if (spaceAfterHost < 0)
            return false;

        var hostnameMemory = source.Slice(cursor, spaceAfterHost);
        cursor += spaceAfterHost + 1;

        // ── 4. MikroTik-расширение: ": " после hostname ───────────────────────
        // bsd-syslog=no:  "hostname : topic,severity message"
        // bsd-syslog=yes: "hostname message"
        bool hasMikroTikTag = cursor + 1 < span.Length
                           && span[cursor]     == (byte)':'
                           && span[cursor + 1] == (byte)' ';
        if (hasMikroTikTag)
            cursor += 2;

        // ── 5. Topic и Severity ───────────────────────────────────────────────
        var topicMemory = ReadOnlyMemory<byte>.Empty;
        var severity    = PriToSeverity(priority & 7);

        if (hasMikroTikTag)
        {
            int spaceAfterTag = span[cursor..].IndexOf((byte)' ');
            if (spaceAfterTag > 0)
            {
                var tagSpan = span.Slice(cursor, spaceAfterTag);
                int commaAt = tagSpan.IndexOf((byte)',');
                if (commaAt > 0)
                {
                    topicMemory = source.Slice(cursor, commaAt);
                    severity    = TextToSeverity(tagSpan[(commaAt + 1)..]);
                    cursor     += spaceAfterTag + 1;
                }
            }
        }

        // ── 6. Message ────────────────────────────────────────────────────────
        if (cursor > source.Length)
            return false;

        entry = new LogEntry
        {
            Priority     = priority,
            TimestampRaw = timestampMemory,
            Hostname     = hostnameMemory,
            Topic        = topicMemory,
            Severity     = severity,
            Message      = TrimNewline(source, cursor),
            ReceivedAt   = receivedAt,
        };

        return true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// RFC 3164 numeric severity (PRI mod 8) → enum.
    /// Используется для bsd-syslog=yes где текстового тега нет.
    /// </summary>
    private static SyslogSeverity PriToSeverity(int s) => s switch
    {
        0 or 1 or 2 => SyslogSeverity.Critical,
        3            => SyslogSeverity.Error,
        4            => SyslogSeverity.Warning,
        5 or 6       => SyslogSeverity.Info,
        7            => SyslogSeverity.Debug,
        _            => SyslogSeverity.Unknown,
    };

    /// <summary>Текстовый severity из MikroTik-тега. "info"u8 — compile-time литерал, ноль аллокаций.</summary>
    private static SyslogSeverity TextToSeverity(ReadOnlySpan<byte> s)
    {
        if (s.SequenceEqual("info"u8))     return SyslogSeverity.Info;
        if (s.SequenceEqual("warning"u8))  return SyslogSeverity.Warning;
        if (s.SequenceEqual("error"u8))    return SyslogSeverity.Error;
        if (s.SequenceEqual("debug"u8))    return SyslogSeverity.Debug;
        if (s.SequenceEqual("critical"u8)) return SyslogSeverity.Critical;
        return SyslogSeverity.Unknown;
    }

    private static ReadOnlyMemory<byte> TrimNewline(ReadOnlyMemory<byte> source, int start)
    {
        var span = source.Span;
        int end  = source.Length;
        while (end > start && (span[end - 1] == '\r' || span[end - 1] == '\n'))
            end--;
        return source.Slice(start, end - start);
    }
}
