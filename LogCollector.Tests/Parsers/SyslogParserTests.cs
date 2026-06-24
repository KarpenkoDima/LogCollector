using System.Runtime.InteropServices;
using System.Text;
using LogCollector.Core.Domain;
using LogCollector.Infrastructure.Parsers;
using Xunit;

namespace LogCollector.Tests.Parsers;

/// <summary>
/// Unit tests for <see cref="SyslogParser.TryParse"/>.
///
/// Two categories of tests live here:
/// 1. Correctness — does each field contain the right value?
/// 2. Architecture — does the parser actually avoid copying bytes?
///    The "ZeroCopy" test uses MemoryMarshal to peek inside ReadOnlyMemory&lt;byte&gt;
///    and confirm every field points into the original buffer.
/// </summary>
public sealed class SyslogParserTests
{
    // The canonical sample line from log-formats.md.
    // All happy-path assertions are derived from this single line to keep tests
    // in sync with the specification without duplication.
    private const string SampleLine =
        "<30>Jun  4 18:00:00 mtk-router : firewall,info " +
        "forward: in:ether1 out:bridge, src-mac 00:11:22:33:44:55, " +
        "proto TCP (SYN), 192.168.88.100:55000 -> 10.0.0.5:80, len 60";

    // ── Happy-path ────────────────────────────────────────────────────────────

    [Fact]
    public void TryParse_ValidMikroTikLine_ExtractsAllSixFields()
    {
        // Arrange: capture the time window so we can assert ReceivedAt is plausible.
        var before = DateTimeOffset.UtcNow;
        bool ok = ParseString(SampleLine, out var entry);
        var after = DateTimeOffset.UtcNow;

        Assert.True(ok);
        Assert.Equal(30,                   entry.Priority);
        Assert.Equal("Jun  4 18:00:00",    ToUtf8(entry.TimestampRaw));
        Assert.Equal("mtk-router",         ToUtf8(entry.Hostname));
        Assert.Equal("firewall",           ToUtf8(entry.Topic));
        Assert.Equal(SyslogSeverity.Info,  entry.Severity);
        Assert.Equal(
            "forward: in:ether1 out:bridge, src-mac 00:11:22:33:44:55, " +
            "proto TCP (SYN), 192.168.88.100:55000 -> 10.0.0.5:80, len 60",
            ToUtf8(entry.Message));

        // ReceivedAt is the collector's clock, injected at parse time.
        Assert.InRange(entry.ReceivedAt, before, after);
    }

    [Fact]
    public void TryParse_SingleDigitDay_TimestampSliceIsExactly15Bytes()
    {
        // RFC 3164 pads single-digit days with a space: "Jun  4", not "Jun 04".
        // An off-by-one in the 15-byte timestamp slice would eat the first character
        // of the hostname, silently corrupting every subsequent field.
        const string line = "<6>Jun  4 09:01:05 fw : system,info rebooted";

        bool ok = ParseString(line, out var entry);

        Assert.True(ok);
        Assert.Equal("Jun  4 09:01:05", ToUtf8(entry.TimestampRaw));  // exactly 15 bytes
        Assert.Equal("fw",              ToUtf8(entry.Hostname));       // hostname must be intact
        Assert.Equal("rebooted",        ToUtf8(entry.Message));
    }

    [Fact]
    public void TryParse_DoubleDigitDay_TimestampSliceIsAlso15Bytes()
    {
        // Both single-digit and double-digit days produce 15-byte timestamps.
        // "Jun 14" (one space) and "Jun  4" (two spaces) are the same length
        // because the spec pads on the left rather than zero-padding.
        const string line = "<30>Jun 14 18:00:00 switch : interface,warning link-down";

        bool ok = ParseString(line, out var entry);

        Assert.True(ok);
        Assert.Equal("Jun 14 18:00:00", ToUtf8(entry.TimestampRaw));
        Assert.Equal("switch",          ToUtf8(entry.Hostname));
    }

    // ── Severity mapping ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("debug",    SyslogSeverity.Debug)]
    [InlineData("info",     SyslogSeverity.Info)]
    [InlineData("warning",  SyslogSeverity.Warning)]
    [InlineData("error",    SyslogSeverity.Error)]
    [InlineData("critical", SyslogSeverity.Critical)]
    public void TryParse_EachKnownSeverityToken_MapsToCorrectEnum(
        string token, SyslogSeverity expected)
    {
        // Tests the ClassifySeverity helper inside the parser indirectly.
        // The "u8 literal + SequenceEqual" technique must cover all five values.
        string line = $"<30>Jun  4 18:00:00 fw : system,{token} something happened";

        bool ok = ParseString(line, out var entry);

        Assert.True(ok);
        Assert.Equal(expected, entry.Severity);
    }

    [Fact]
    public void TryParse_UnrecognisedSeverityToken_ReturnsUnknown_AndEntryIsNotDropped()
    {
        // MikroTik could in theory emit a severity we have not seen.
        // The parser must not throw and must not silently fail — it should return
        // a valid entry with Severity = Unknown so no log line is lost.
        const string line = "<30>Jun  4 18:00:00 fw : system,notice something unusual";

        bool ok = ParseString(line, out var entry);

        Assert.True(ok);                               // entry is NOT dropped
        Assert.Equal(SyslogSeverity.Unknown, entry.Severity);
        Assert.Equal("something unusual",    ToUtf8(entry.Message));
    }

    // ── Edge cases ────────────────────────────────────────────────────────────

    [Fact]
    public void TryParse_TrailingCrLf_IsStrippedFromMessageField()
    {
        // Some UDP stacks append \r\n to the datagram payload.
        // The message stored in LogEntry must be clean text — no control characters.
        const string line = "<30>Jun  4 18:00:00 fw : dhcp,info lease assigned\r\n";

        bool ok = ParseString(line, out var entry);

        Assert.True(ok);
        Assert.Equal("lease assigned", ToUtf8(entry.Message));  // no trailing whitespace
    }

    [Fact]
    public void TryParse_MessageContainsColon_IsNotTruncatedAtColon()
    {
        // The message body itself often contains colons (firewall sub-format).
        // Only the first " : " (space-colon-space) after the timestamp should be
        // treated as the MikroTik hostname/topic separator.
        const string line =
            "<30>Jun  4 18:00:00 fw : firewall,info forward: in:ether1 out:bridge";

        bool ok = ParseString(line, out var entry);

        Assert.True(ok);
        Assert.Equal("fw",                         ToUtf8(entry.Hostname));
        Assert.Equal("forward: in:ether1 out:bridge", ToUtf8(entry.Message));
    }

    // ── Invalid input ─────────────────────────────────────────────────────────

    [Fact]
    public void TryParse_EmptyInput_ReturnsFalse_AndEntryIsDefault()
    {
        bool ok = SyslogParser.TryParse(
            ReadOnlyMemory<byte>.Empty, DateTimeOffset.UtcNow, out var entry);

        Assert.False(ok);
        Assert.Equal(default, entry);  // struct must be zeroed on failure
    }

    [Fact]
    public void TryParse_MissingClosingAngleBracket_ReturnsFalse()
    {
        // "<30Jun  4..." has no '>' — the PRI field is malformed.
        bool ok = ParseString("<30Jun  4 18:00:00 fw : system,info msg", out _);
        Assert.False(ok);
    }

    [Fact]
    public void TryParse_MissingMikroTikSeparator_ReturnsFalse()
    {
        // Standard RFC 3164 lines use "HOSTNAME TOPIC[PID]: MESSAGE" with no " : ".
        // We only support the MikroTik extension — other formats return false.
        bool ok = ParseString("<30>Jun  4 18:00:00 fw system info msg", out _);
        Assert.False(ok);
    }

    [Fact]
    public void TryParse_MissingCommaInTopicSeverity_ReturnsFalse()
    {
        // After the MikroTik separator we expect "TOPIC,SEVERITY".
        // If there is no comma, we cannot extract the topic.
        bool ok = ParseString("<30>Jun  4 18:00:00 fw : system-info msg", out _);
        Assert.False(ok);
    }

    // ── Zero-copy architectural proof ─────────────────────────────────────────

    [Fact]
    public void TryParse_AllMemoryFields_AreSlicesOfSourceBuffer_NoBytesAreCopied()
    {
        // This test directly verifies the zero-copy architectural guarantee.
        //
        // How it works: MemoryMarshal.TryGetArray reveals the underlying byte[]
        // and the offset/count of any ReadOnlyMemory<byte> backed by a managed array.
        // If the backing array IS the same object reference as sourceArray,
        // no copy occurred — the parser sliced in-place.
        //
        // This matters because at 10 000 entries/sec, even a single extra byte[]
        // allocation per field would create 50 000 Gen0 objects per second.
        byte[] sourceArray = Encoding.UTF8.GetBytes(SampleLine);

        bool ok = SyslogParser.TryParse(
            sourceArray.AsMemory(), DateTimeOffset.UtcNow, out var entry);

        Assert.True(ok);

        // Extract ArraySegment from each Memory<byte> field.
        Assert.True(MemoryMarshal.TryGetArray(entry.TimestampRaw, out var tsSegment));
        Assert.True(MemoryMarshal.TryGetArray(entry.Hostname,     out var hostSegment));
        Assert.True(MemoryMarshal.TryGetArray(entry.Topic,        out var topicSegment));
        Assert.True(MemoryMarshal.TryGetArray(entry.Message,      out var msgSegment));

        // The backing array must be the SAME object — same reference, not an equal copy.
        Assert.Same(sourceArray, tsSegment.Array);
        Assert.Same(sourceArray, hostSegment.Array);
        Assert.Same(sourceArray, topicSegment.Array);
        Assert.Same(sourceArray, msgSegment.Array);

        // Bonus: verify exact byte positions in the source buffer.
        // <30> = 4 bytes, so timestamp starts at offset 4.
        Assert.Equal(4,  tsSegment.Offset);
        Assert.Equal(15, tsSegment.Count);   // RFC 3164 timestamp is always 15 bytes

        // "mtk-router" starts right after "Jun  4 18:00:00 " (4+15+1 = 20)
        Assert.Equal(20, hostSegment.Offset);
        Assert.Equal(10, hostSegment.Count);  // "mtk-router".Length == 10

        // "firewall" starts after "mtk-router : " (20+10+3 = 33)
        Assert.Equal(33, topicSegment.Offset);
        Assert.Equal(8,  topicSegment.Count); // "firewall".Length == 8

        // Message starts after "firewall,info " (33+8+1+4+1 = 47)
        Assert.Equal(47, msgSegment.Offset);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>UTF-8 encodes <paramref name="line"/> and calls TryParse.</summary>
    private static bool ParseString(string line, out LogEntry entry)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line);
        return SyslogParser.TryParse(bytes.AsMemory(), DateTimeOffset.UtcNow, out entry);
    }

    /// <summary>Decodes <paramref name="memory"/> back to a UTF-8 string for assertions.</summary>
    private static string ToUtf8(ReadOnlyMemory<byte> memory)
        => Encoding.UTF8.GetString(memory.Span);
}
