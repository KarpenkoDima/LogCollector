using System.Text;
using LogCollector.Core.Domain;
using LogCollector.Infrastructure.Parsing;
using Xunit;

namespace LogCollector.Tests.Parsing;

public sealed class Rfc3164ParserTests
{
    private readonly Rfc3164Parser _parser = new();
    private static readonly DateTimeOffset ReceivedAt = new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ParsesStandardBsdMessage()
    {
        byte[] bytes = "<30>Jul 14 12:30:45 router-1 system rebooted\r\n"u8.ToArray();

        bool parsed = _parser.TryParse(bytes, ReceivedAt, out LogEntry entry);

        Assert.True(parsed);
        Assert.Equal(30, entry.Priority);
        Assert.Equal(3, entry.Facility);
        Assert.Equal(SyslogSeverity.Informational, entry.Severity);
        Assert.Equal("Jul 14 12:30:45", Text(entry.Timestamp));
        Assert.Equal("router-1", Text(entry.Hostname));
        Assert.True(entry.Topic.IsEmpty);
        Assert.Equal("system rebooted", Text(entry.Message));
        Assert.Equal(ReceivedAt, entry.ReceivedAt);
    }

    [Fact]
    public void ParsesSingleDigitDayAndMikroTikTag()
    {
        byte[] bytes = "<132>Jun  4 18:00:00 mtk : firewall,info forward: in:ether1"u8.ToArray();

        bool parsed = _parser.TryParse(bytes, ReceivedAt, out LogEntry entry);

        Assert.True(parsed);
        Assert.Equal("firewall", Text(entry.Topic));
        Assert.Equal("forward: in:ether1", Text(entry.Message));
        Assert.Equal(SyslogSeverity.Warning, entry.Severity);
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain text")]
    [InlineData("<>Jul 14 12:30:45 router message")]
    [InlineData("<+30>Jul 14 12:30:45 router message")]
    [InlineData("<192>Jul 14 12:30:45 router message")]
    [InlineData("<30>Foo 14 12:30:45 router message")]
    [InlineData("<30>Jul 32 12:30:45 router message")]
    [InlineData("<30>Jul 14 24:30:45 router message")]
    [InlineData("<30>Jul 14 12:30:45")]
    public void RejectsMalformedMessages(string value)
    {
        Assert.False(_parser.TryParse(Encoding.UTF8.GetBytes(value), ReceivedAt, out _));
    }

    [Fact]
    public void FieldsAreSlicesOfOriginalBuffer()
    {
        byte[] bytes = "<30>Jul 14 12:30:45 router message"u8.ToArray();
        Assert.True(_parser.TryParse(bytes, ReceivedAt, out LogEntry entry));

        bytes[^1] = (byte)'!';

        Assert.Equal("messag!", Text(entry.Message));
    }

    [Fact]
    public void DoesNotTreatOrdinaryColonPrefixedMessageAsMikroTikTag()
    {
        byte[] bytes = "<30>Jul 14 12:30:45 router : ordinary message"u8.ToArray();

        Assert.True(_parser.TryParse(bytes, ReceivedAt, out LogEntry entry));
        Assert.True(entry.Topic.IsEmpty);
        Assert.Equal(": ordinary message", Text(entry.Message));
    }

    private static string Text(ReadOnlyMemory<byte> value) => Encoding.UTF8.GetString(value.Span);
}
