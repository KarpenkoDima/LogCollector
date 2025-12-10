namespace LogCollector.Infrastructure.Listeners;

/// <summary>
/// Configuration for <see cref="UdpSyslogListener"/>.
/// Bind this to the "SyslogListener" section in <c>appsettings.json</c>:
/// <code>
/// "SyslogListener": {
///   "Port": 514,
///   "MaxDatagramSize": 8192
/// }
/// </code>
/// </summary>
public sealed class SyslogListenerOptions
{
    /// <summary>
    /// UDP port to listen on.  The standard syslog port is 514.
    /// Note: binding to ports below 1024 requires root/CAP_NET_BIND_SERVICE on Linux.
    /// For development, use a port ≥ 1024 (e.g. 5140) and configure your router to match.
    /// </summary>
    public int Port { get; set; } = 514;

    /// <summary>
    /// Maximum UDP datagram size, in bytes.  This determines the size of the single
    /// pinned receive buffer that lives for the service lifetime.
    ///
    /// RFC 5426 recommends 480 bytes for compatibility with all IPv4 paths (576 byte MTU
    /// minus IP and UDP headers).  In practice, MikroTik devices and modern networks
    /// handle larger messages.  8192 bytes is a safe default — it fits any realistic
    /// syslog message while staying well below the 65507-byte UDP payload limit.
    /// </summary>
    public int MaxDatagramSize { get; set; } = 8_192;
}
