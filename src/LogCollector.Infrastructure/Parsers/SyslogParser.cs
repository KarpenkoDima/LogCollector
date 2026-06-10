using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using LogCollector.Core.Interfaces;
using LogCollector.Core.Models;

namespace LogCollector.Infrastructure.Parsers;

/// <summary>
/// Парсер RFC 3164 (BSD Syslog).
/// Формат: &lt;PRIORITY&gt;TIMESTAMP HOSTNAME TAG: MESSAGE
/// Пример: &lt;34&gt;Oct 11 22:14:15 router dhcp: assigned 192.168.1.10
///
/// Весь парсинг идёт через Span — ни одной аллокации до момента
/// создания LogEntry, где string неизбежен.
/// </summary>
public sealed class SyslogParser : ILogParser
{
    // Таблица маппинга severity (нижние 3 бита priority) в строку.
    // Статический массив — живёт в памяти один раз на всё время работы сервиса.
    private static readonly string[] SeverityMap =
        ["emergency", "alert", "critical", "error", "warning", "notice", "info", "debug"];

    // Кэш IP → string: ToString() только для новых адресов.
    // Роутеров в сети единицы — кэш из 64 записей покроет все реальные случаи.
    // используем ConcurrentDictionary для потокобезопасности
    private readonly ConcurrentDictionary<IPAddress, string> _ipCache = new(concurrencyLevel: Environment.ProcessorCount, capacity:64);

    public LogEntry? TryParse(ReadOnlyMemory<byte> data, IPEndPoint remoteEndpoint)
    {
        var span = data.Span;
        if (span.IsEmpty) return null;

        // Шаг 1: извлекаем PRIORITY — число между '<' и '>'
        if (false == TryParsePriority(span, out var priority, out var afterPriority))
            return null;

        var severity = priority & 0x07;  // нижние 3 бита
        var level    = SeverityMap[severity];

        // Шаг 2: пропускаем TIMESTAMP (15 символов: "Oct 11 22:14:15") + пробел
        var rest = span[afterPriority..];
        if (rest.Length < 16) return null;

        var messageSpan = rest[16..];

        // Шаг 3: извлекаем HOSTNAME — до первого пробела
        var hostnameEnd = messageSpan.IndexOf((byte)' ');
        if (hostnameEnd < 0) return null;

        // ВАЖНО: Мы вынуждены аллоцировать string здесь, так как буфер 'data'
        // принадлежит PipeReader/UDP Socketи будет перезаписан сразу после return.
        // Аллоцируем строки только здесь — только для полей которые идут в БД
        var hostname = Encoding.ASCII.GetString(messageSpan[..hostnameEnd]);
        var msgSpan = messageSpan[(hostnameEnd + 1)..];

        // Обрезаем \r\n прямо в байтах — до ToString()
        while (msgSpan.Length > 0 && (msgSpan[^1] == '\n' || msgSpan[^1] == '\r'))
            msgSpan = msgSpan[..^1];
        var message  = Encoding.UTF8.GetString(msgSpan);

        return new LogEntry
        {
            Source    = "mikrotik",
            Timestamp = DateTime.UtcNow,
            Level     = level,
            Message   = message,
            SourceIp  = GetCachedIp(remoteEndpoint),
            Hostname  = hostname
        };
    }

    private static bool TryParsePriority(
        ReadOnlySpan<byte> span,
        out int priority,
        out int bytesConsumed)
    {
        priority      = 0;
        bytesConsumed = 0;

        if (span[0] != (byte)'<') return false;

        var closeAngle = span.IndexOf((byte)'>');
        if (closeAngle < 2) return false;

        // Utf8Parser.TryParse — парсит число прямо из байт, без ToString()
        if (false == Utf8Parser.TryParse(span[1..closeAngle], out priority, out _))
            return false;

        bytesConsumed = closeAngle + 1;
        return true;
    }

    private string GetCachedIp(IPEndPoint address)
    {
        return _ipCache.GetOrAdd(address.Address, addr => addr.ToString());
    }
}
