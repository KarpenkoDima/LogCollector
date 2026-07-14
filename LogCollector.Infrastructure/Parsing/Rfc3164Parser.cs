using System.Buffers.Text;
using LogCollector.Application.Interfaces;
using LogCollector.Core.Domain;

namespace LogCollector.Infrastructure.Parsing;

/// <summary>
/// Parses RFC 3164/BSD syslog and the RouterOS extended tag without regexes or strings.
/// Expected forms:
/// <c>&lt;PRI&gt;Mmm dd HH:mm:ss host message</c> and
/// <c>&lt;PRI&gt;Mmm dd HH:mm:ss host : topic,severity message</c>.
/// </summary>
public sealed class Rfc3164Parser : ILogParser
{
    private const int TimestampLength = 15;
    private static ReadOnlySpan<byte> Months => "JanFebMarAprMayJunJulAugSepOctNovDec"u8;

    public bool TryParse(
        ReadOnlyMemory<byte> payload,
        DateTimeOffset receivedAt,
        out LogEntry entry)
    {
        entry = default;
        payload = TrimEnd(payload);
        ReadOnlySpan<byte> source = payload.Span;

        if (source.Length < 1 + 1 + 1 + TimestampLength + 1 + 1 + 1 || source[0] != '<')
            return false;

        int close = source.IndexOf((byte)'>');
        if (close is < 2 or > 4 ||
            !Utf8Parser.TryParse(source[1..close], out int priority, out int consumed) ||
            consumed != close - 1 || priority is < 0 or > 191)
        {
            return false;
        }

        int cursor = close + 1;
        if (cursor + TimestampLength >= source.Length ||
            !IsTimestamp(source.Slice(cursor, TimestampLength)) ||
            source[cursor + TimestampLength] != ' ')
        {
            return false;
        }

        ReadOnlyMemory<byte> timestamp = payload.Slice(cursor, TimestampLength);
        cursor += TimestampLength + 1;

        int hostLength = source[cursor..].IndexOf((byte)' ');
        if (hostLength <= 0)
            return false;

        ReadOnlyMemory<byte> hostname = payload.Slice(cursor, hostLength);
        cursor += hostLength;
        cursor = SkipSpaces(source, cursor);

        ReadOnlyMemory<byte> topic = ReadOnlyMemory<byte>.Empty;
        if (cursor < source.Length && source[cursor] == ':')
        {
            cursor = SkipSpaces(source, cursor + 1);
            int tagLength = source[cursor..].IndexOf((byte)' ');
            if (tagLength > 0)
            {
                ReadOnlySpan<byte> tag = source.Slice(cursor, tagLength);
                int comma = tag.IndexOf((byte)',');
                if (comma > 0)
                    topic = payload.Slice(cursor, comma);

                cursor += tagLength;
                cursor = SkipSpaces(source, cursor);
            }
        }

        entry = new LogEntry
        {
            Priority = priority,
            Timestamp = timestamp,
            Hostname = hostname,
            Topic = topic,
            Message = payload[cursor..],
            ReceivedAt = receivedAt,
        };
        return true;
    }

    private static bool IsTimestamp(ReadOnlySpan<byte> value)
    {
        if (value.Length != TimestampLength || value[3] != ' ' || value[6] != ' ' ||
            value[9] != ':' || value[12] != ':')
        {
            return false;
        }

        bool knownMonth = false;
        for (int i = 0; i < Months.Length; i += 3)
            knownMonth |= value[..3].SequenceEqual(Months.Slice(i, 3));

        if (!knownMonth || !TryTwoDigits(value[4..6], allowLeadingSpace: true, out int day) ||
            !TryTwoDigits(value[7..9], allowLeadingSpace: false, out int hour) ||
            !TryTwoDigits(value[10..12], allowLeadingSpace: false, out int minute) ||
            !TryTwoDigits(value[13..15], allowLeadingSpace: false, out int second))
        {
            return false;
        }

        return day is >= 1 and <= 31 && hour <= 23 && minute <= 59 && second <= 59;
    }

    private static bool TryTwoDigits(
        ReadOnlySpan<byte> value,
        bool allowLeadingSpace,
        out int result)
    {
        byte first = value[0];
        byte second = value[1];
        if (second is < (byte)'0' or > (byte)'9' ||
            (first is < (byte)'0' or > (byte)'9' && !(allowLeadingSpace && first == ' ')))
        {
            result = 0;
            return false;
        }

        result = (first == ' ' ? 0 : first - '0') * 10 + second - '0';
        return true;
    }

    private static int SkipSpaces(ReadOnlySpan<byte> source, int cursor)
    {
        while (cursor < source.Length && source[cursor] == ' ')
            cursor++;
        return cursor;
    }

    private static ReadOnlyMemory<byte> TrimEnd(ReadOnlyMemory<byte> payload)
    {
        ReadOnlySpan<byte> source = payload.Span;
        int length = source.Length;
        while (length > 0 && source[length - 1] is (byte)'\0' or (byte)'\r' or (byte)'\n')
            length--;
        return payload[..length];
    }
}
