namespace LogCollector.Core.Domain;

/// <summary>RFC 3164 severity encoded in the low three bits of PRI.</summary>
public enum SyslogSeverity : byte
{
    Emergency = 0,
    Alert = 1,
    Critical = 2,
    Error = 3,
    Warning = 4,
    Notice = 5,
    Informational = 6,
    Debug = 7,
}
