namespace LogCollector.Core.Domain;

/// <summary>
/// MikroTik syslog severity levels as they appear in the TOPIC,SEVERITY field
/// (e.g. "firewall,info" → <see cref="Info"/>).
///
/// Ordered from least to most severe so that numeric comparisons are meaningful:
/// <c>severity >= SyslogSeverity.Warning</c> is a valid filter expression.
///
/// Backed by <c>byte</c> rather than <c>int</c> to shave 3 bytes off every
/// <see cref="LogEntry"/> struct — small per entry, meaningful at 10 000 entries/sec.
/// </summary>
public enum SyslogSeverity : byte
{
    /// <summary>Severity token was not in the known set — treat as lowest priority.</summary>
    Unknown  = 0,

    Debug    = 1,
    Info     = 2,
    Warning  = 3,
    Error    = 4,
    Critical = 5,
}
