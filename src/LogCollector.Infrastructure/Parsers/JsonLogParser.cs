using System.Net;
using System.Text.Json;
using LogCollector.Core.Interfaces;
using LogCollector.Core.Models;

namespace LogCollector.Infrastructure.Parsers;

/// <summary>
/// Парсер JSON-логов от Winlogbeat (newline-delimited JSON).
/// Использует Utf8JsonReader — работает прямо с байтами,
/// без промежуточного преобразования в string.
///
/// Winlogbeat шлёт ~40 полей. Мы читаем только 4 нужных,
/// остальные 36 пропускаем через reader.Skip() без аллокаций.
/// </summary>
public sealed class JsonLogParser : ILogParser
{
    public bool TryParse(ReadOnlyMemory<byte> data, IPEndPoint remoteEndpoint, out LogEntry entry)
    {
        // Utf8JsonReader — ref struct, живёт только на стеке, работает прямо со Span
        var reader = new Utf8JsonReader(data.Span);

        entry = default;
        string? timestamp = null;
        string? level     = null;
        string? message   = null;
        string? hostname  = null;

        try
        {
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;

                // ValueTextEquals — сравнение байт к байту без GetString().
                // "..."u8 — литерал C# 11: статические байты в бинарнике, ноль аллокаций.
                if (reader.ValueTextEquals("@timestamp"u8))
                {
                    reader.Read();
                    timestamp = reader.GetString();
                }
                else if (reader.ValueTextEquals("log.level"u8))
                {
                    reader.Read();
                    level = reader.GetString();
                }
                else if (reader.ValueTextEquals("message"u8))
                {
                    reader.Read();
                    message = reader.GetString();
                }
                else if (reader.ValueTextEquals("host"u8))
                {
                    reader.Read(); // переходим к значению (StartObject)
                    hostname = ParseHostObject(ref reader);
                }
                else
                {
                    reader.Read();
                    // Skip() пропускает поле целиком, включая вложенные объекты/массивы
                    reader.Skip();
                }
            }
        }
        catch (JsonException)
        {
            // Невалидный JSON — не бросаем дальше, просто отбрасываем пакет
            return false;
        }

        // message обязательно, остальное подставляем по умолчанию
        if (message is null) return false;

        entry =  new LogEntry
        {
            Source    = "winlogbeat",
            Timestamp = DateTime.TryParse(timestamp, out var ts) ? ts : DateTime.UtcNow,
            Level     = level ?? "info",
            Message   = message,
            SourceIp  = remoteEndpoint.Address.ToString(),
            Hostname  = hostname
        };
        return true;
    }

    private static string? ParseHostObject(ref Utf8JsonReader reader)
    {
        // ref обязателен: Utf8JsonReader — ref struct, хранит позицию в буфере.
        // Без ref получили бы копию и потеряли позицию после выхода из метода.
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            var isName = reader.ValueTextEquals("name"u8);
            reader.Read();

            if (isName) return reader.GetString();

            reader.Skip();
        }
        return null;
    }
}
