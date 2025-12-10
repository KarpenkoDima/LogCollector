using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using LogCollector.Application.Interfaces;
using LogCollector.Core.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LogCollector.Infrastructure.Listeners;

/// <summary>
/// A <see cref="BackgroundService"/> that binds a UDP socket and feeds parsed
/// <see cref="LogEntry"/> values into the bounded pipeline channel.
///
/// <para>
/// This class knows nothing about syslog, WinBeat, or any other log format.
/// It delegates all parsing to the injected <see cref="ILogParser"/>, which is
/// a <c>CompositeLogParser</c> in production.  Adding a new format requires
/// zero changes here.
/// </para>
/// </summary>
public sealed class UdpSyslogListener : BackgroundService
{
    private readonly byte[] _pinnedReceiveBuffer;
    private readonly ChannelWriter<LogEntry> _writer;
    private readonly ILogParser _parser;
    private readonly ILogger<UdpSyslogListener> _logger;
    private readonly SyslogListenerOptions _options;

    public UdpSyslogListener(
        ChannelWriter<LogEntry> writer,
        ILogParser parser,
        IOptions<SyslogListenerOptions> options,
        ILogger<UdpSyslogListener> logger)
    {
        _writer  = writer;
        _parser  = parser;
        _logger  = logger;
        _options = options.Value;

        _pinnedReceiveBuffer = GC.AllocateArray<byte>(_options.MaxDatagramSize, pinned: true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var socket = CreateBoundSocket(_options.Port);

        Memory<byte> receiveWindow = _pinnedReceiveBuffer;
        EndPoint sender = new IPEndPoint(IPAddress.Any, 0);

        _logger.LogInformation(
            "UDP listener bound to 0.0.0.0:{Port} (max datagram: {Max} bytes)",
            _options.Port, _options.MaxDatagramSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await socket
                    .ReceiveFromAsync(receiveWindow, SocketFlags.None, sender, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException ex)
            {
                _logger.LogWarning(ex,
                    "Socket error (SocketError={Code}); continuing", ex.SocketErrorCode);
                continue;
            }

            int length = received.ReceivedBytes;

            IMemoryOwner<byte>? datagramOwner = MemoryPool<byte>.Shared.Rent(length);
            bool ownershipTransferred = false;

            try
            {
                _pinnedReceiveBuffer.AsSpan(0, length).CopyTo(datagramOwner.Memory.Span);

                ReadOnlyMemory<byte> datagram = datagramOwner.Memory[..length];

                // _parser is a CompositeLogParser — it tries each registered format in order.
                // No format-specific code lives here.
                if (!_parser.TryParse(datagram, DateTimeOffset.UtcNow, out var entry))
                {
                    _logger.LogDebug(
                        "No parser matched datagram ({Length} B) from {Sender}",
                        length, received.RemoteEndPoint);
                    continue;
                }

                entry = entry with { RawBuffer = datagramOwner };
                await _writer.WriteAsync(entry, stoppingToken).ConfigureAwait(false);
                ownershipTransferred = true;
            }
            finally
            {
                if (!ownershipTransferred)
                    datagramOwner?.Dispose();
            }
        }

        _logger.LogInformation("UDP listener stopped");
    }

    private static Socket CreateBoundSocket(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Bind(new IPEndPoint(IPAddress.Any, port));
        return socket;
    }
}
