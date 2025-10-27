namespace LogCollector.Core.Models;

/// <summary>
/// Размер LogEntry составляет 48 байт. Золотое правило .NET (и Microsoft Guidelines): 
/// структуры должны быть не больше 16 байт (максимум 24).
/// При каждом TryRead(out var entry) и batch.Add(entry)
/// происходит физическое копирование 48 байт в памяти.
/// При расширении емкости(Capacity) внутри List<LogEntry> рантайм будет копировать килобайты памяти.
/// Сэкономив 24 байта на Object Header в куче, мы создим нагрузку на пропускную способность памяти и инвалидацию
/// L1/L2 кэша. Сами строки внутри все равно остаются в куче (Heap), 
/// так что от работы GC мы не избавимся. Оставляем sealed class. 
/// </summary>
public sealed class LogEntry
{
    public required string Source { get; init; }    
    public required DateTime Timestamp { get; init; }
    public required string Level { get; init; }
    public required string Message { get; init; }
    public string? SourceIp { get; init; }
    public string? Hostname { get; init; }
}
