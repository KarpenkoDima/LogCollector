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
///
/// <para><b>Hardening (P1.4):</b> cheap structural checks reject malformed
/// datagrams without turning the parser into a full RFC validator:</para>
/// <list type="bullet">
///   <item>PRI is all digits and Utf8Parser consumes every PRI byte;</item>
///   <item>PRI is in the valid syslog range [0, 191];</item>
///   <item>timestamp is exactly 15 bytes followed by a space separator;</item>
///   <item>hostname is non-empty;</item>
///   <item>the cursor never advances past the source buffer.</item>
/// </list>
/// <para>Unknown MikroTik topic/severity stay permissive (mapped to Unknown,
/// never dropped). No regex, no string allocation on the hot path.</para>
/// </summary>
public static class SyslogParser
{
    private const int TimestampByteLength = 15;

    // Max valid syslog PRI: 23 (facility local7) * 8 + 7 (severity debug) = 191.
    private const int MaxPriority = 191;

    public static bool TryParse(
        ReadOnlyMemory<byte> source,
        DateTimeOffset receivedAt,
        out LogEntry entry)
    {
        entry = default;
        var span = source.Span;

        if (span.IsEmpty || span[0] != (byte)'<')
            return false;

        // ── 1. PRI ────────────────────────────────────────────────────────────
        int angleClose = span.IndexOf((byte)'>');
        if (angleClose <= 1)               // need at least one digit between < and >
            return false;

        var priSpan = span[1..angleClose];

        // P1.4: PRI must be all digits. Utf8Parser accepts a leading '+'/'-' sign,
        // which is invalid for a syslog PRI — reject anything not starting with a
        // digit before parsing. (Cheap: one byte check, no allocation.)
        if (priSpan.IsEmpty || priSpan[0] is < (byte)'0' or > (byte)'9')
            return false;

        // Utf8Parser.TryParse stops at the first non-digit and reports bytesConsumed;
        // if it consumed fewer bytes than priSpan.Length, there was garbage ('12x').
        if (!Utf8Parser.TryParse(priSpan, out int priority, out int priConsumed))
            return false;
        if (priConsumed != priSpan.Length)
            return false;

        // P1.4: PRI in valid syslog range [0, 191].
        if (priority is < 0 or > MaxPriority)
            return false;

        int cursor = angleClose + 1;

        // ── 2. Timestamp — RFC 3164: ровно 15 байт + пробел-разделитель ──────
        // Need 15 timestamp bytes AND the separator space after them.
        if (cursor + TimestampByteLength >= span.Length)
            return false;

        // P1.4: the byte right after the 15-byte timestamp must be a space.
        if (span[cursor + TimestampByteLength] != (byte)' ')
            return false;

        var timestampMemory = source.Slice(cursor, TimestampByteLength);
        cursor += TimestampByteLength + 1;

        // ── 3. Hostname — до первого пробела, НЕ пустой ──────────────────────
        if (cursor >= span.Length)         // P1.4: cursor bound check
            return false;

        int spaceAfterHost = span[cursor..].IndexOf((byte)' ');
        if (spaceAfterHost <= 0)           // P1.4: <0 not found, ==0 empty hostname
            return false;

        var hostnameMemory = source.Slice(cursor, spaceAfterHost);
        cursor += spaceAfterHost + 1;

        // ── 4. MikroTik-расширение: ": " после hostname ───────────────────────
        bool hasMikroTikTag = cursor + 1 < span.Length
                           && span[cursor]     == (byte)':'
                           && span[cursor + 1] == (byte)' ';
        if (hasMikroTikTag)
            cursor += 2;

        // ── 5. Topic и Severity (permissive — сохранено) ─────────────────────
        var topicMemory = ReadOnlyMemory<byte>.Empty;
        var severity    = PriToSeverity(priority & 7);

        if (hasMikroTikTag && cursor < span.Length)
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
        // P1.4: cursor must not exceed source (defensive — all paths above respect it).
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

    private static SyslogSeverity PriToSeverity(int s) => s switch
    {
        0 or 1 or 2 => SyslogSeverity.Critical,
        3            => SyslogSeverity.Error,
        4            => SyslogSeverity.Warning,
        5 or 6       => SyslogSeverity.Info,
        7            => SyslogSeverity.Debug,
        _            => SyslogSeverity.Unknown,
    };

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