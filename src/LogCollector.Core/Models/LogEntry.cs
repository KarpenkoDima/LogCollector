namespace LogCollector.Core.Models;

public sealed class LogEntry
{
    public required string Source { get; init; }    
    public required DateTime Timestamp { get; init; }
    public required string Level { get; init; }
    public required string Message { get; init; }
    public string? SourceIp { get; init; }
    public string? Hostname { get; init; }
}
