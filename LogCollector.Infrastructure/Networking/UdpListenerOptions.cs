namespace LogCollector.Infrastructure.Networking;

public sealed class UdpListenerOptions
{
    public const string SectionName = "UdpListener";

    public string Address { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 5140;
    public int MaxDatagramSize { get; set; } = 8_192;
    public int SocketReceiveBufferSize { get; set; } = 1_048_576;
}
