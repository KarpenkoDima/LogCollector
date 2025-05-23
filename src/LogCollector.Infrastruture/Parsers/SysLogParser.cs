using System.Buffers.Text;
using System.Net;
using System.Text;
using LogCollector.Core.Interfaces;
using LogCollector.Core.Models;

namespace LogCollector.Infrastruture.Parsers;


/// <summary>
/// Парсер RFC 3164 (BSD syslog)
///  Формат: <PRIORITY>TIMESTAMP HOSTNAME TAG: MESSAGE
/// Пример: <34>Oct 11 22:14:15 router dhcp: assigned 192.168.1.12
///
/// Весь парсинг идёт через Span - ни одной аллокации до момента создани\
/// LogEntry, где string неизбежен.
/// </summary>
public class SysLogParser :ILogParser
{
    // Таблица маппинга severity (нижние 3 бита priority) в строку
    private static readonly string[] SeverityMap =
        ["emergency", "alert", "critical", "error", "warning", "notice", "info", "debug"];

    private readonly Dictionary<IPAddress, string> _ipStringCache = new(64);

    private string GetCachedIpString(IPAddress address)
    {
        if (false == _ipStringCache.TryGetValue(address, out var str))
        {
            // ToString() только для новых адресов
            str = address.ToString();
            if (_ipStringCache.Count < 64) // не растём бесконечно
            {
                _ipStringCache[address] = str;
            }
        }

        return str;
    }
    public LogEntry? TryParse(ReadOnlyMemory<byte> data, IPEndPoint remoteEndpoint)
    {
        var span = data.Span;
        if (span.IsEmpty)
        {
            return null;
        }
        
        // Шаг 1: извлекаем PRIORITY - число между '<' и '>'
        if (false == TryParsePriority(span, out var priority, out var afterPriority))
        {
            return null;
        }

        var severity = priority & 0x07; // нижние 3 бита
        var level = SeverityMap[severity];
        
        // шаг 2: пропускаем TIMESTAMP (15 символов: "Oct 11 22:14:15")
        // Упращённо -  не парсим в DateTime, используем UtcNow.
        // Полный парсинг RFC 3164 timestamp добавляем отдельно если нужно.
        var rest = span[afterPriority..];
        if (rest.Length < 16)
        {
            return null;
        }

        var messageSpan = rest[16..]; // пропускаем timestamp + пробел
        
        // Шаг 3: извлекаем HOSTNAME - до первого пробела
        var hostnameEnd = messageSpan.IndexOf((byte)' ');
        if (hostnameEnd < 0)
        {
            return null;
        }
        
        // Только здесь аллоцируем строки - для полей которые ждёт БД
        var hostname = Encoding.ASCII.GetString(messageSpan[..hostnameEnd]);
        var message = Encoding.UTF8.GetString(messageSpan[(hostnameEnd + 1)..]);

        return new LogEntry
        {
            Source = "mikrotik",
            Timestamp = DateTime.UtcNow,
            Level = level,
            Message = message,
            SourceIp = GetCachedIpString(remoteEndpoint.Address),
            Hostname = hostname
        };

    }

    private bool TryParsePriority(ReadOnlySpan<byte> span, out int priority, out int bytesConsumed)
    {
        priority = 0;
        bytesConsumed = 0;

        if (span[0] != (byte)'<')
        {
            return false;
        }

        var closeAngle = span.IndexOf((byte)'>');
        if (closeAngle < 2)
        {
            return false;
        }
        //UtfParser.TryParse - парсит число прямо из байт, без ToString()
        if (false == Utf8Parser.TryParse(span[1..closeAngle],  out priority, out _))
        {
            return false;
        }

        bytesConsumed = closeAngle + 1;
        return true;
    }
}