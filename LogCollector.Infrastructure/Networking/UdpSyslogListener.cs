using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using LogCollector.Application.Interfaces;
using LogCollector.Core.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LogCollector.Infrastructure.Networking;

/// <summary>
/// Receives directly into pool-rented memory. Ownership moves to the channel only
/// after parsing succeeds; every other path returns the buffer to the pool.
/// </summary>
public sealed partial class UdpSyslogListener : BackgroundService
{
    private readonly ChannelWriter<LogEntry> _writer;
    private readonly ILogParser _parser;
    private readonly UdpListenerOptions _options;
    private readonly ILogger<UdpSyslogListener> _logger;

    public UdpSyslogListener(
        ChannelWriter<LogEntry> writer,
        ILogParser parser,
        IOptions<UdpListenerOptions> options,
        ILogger<UdpSyslogListener> logger)
    {
        _writer = writer;
        _parser = parser;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IPAddress address = IPAddress.Parse(_options.Address);
        using var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        socket.ReceiveBufferSize = _options.SocketReceiveBufferSize;
        socket.Bind(new IPEndPoint(address, _options.Port));

        var sender = new SocketAddress(address.AddressFamily);
        LogListening(_logger, address, _options.Port);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(_options.MaxDatagramSize);
                bool transferred = false;
                try
                {
                    int length = await socket.ReceiveFromAsync(
                        owner.Memory[.._options.MaxDatagramSize],
                        SocketFlags.None,
                        sender,
                        stoppingToken).ConfigureAwait(false);

                    ReadOnlyMemory<byte> payload = owner.Memory[..length];
                    if (!_parser.TryParse(payload, DateTimeOffset.UtcNow, out LogEntry entry))
                        continue;

                    entry = entry with { BufferOwner = owner };
                    await _writer.WriteAsync(entry, stoppingToken).ConfigureAwait(false);
                    transferred = true;
                }
                catch (SocketException exception) when (!stoppingToken.IsCancellationRequested)
                {
                    LogReceiveFailure(_logger, exception);
                }
                finally
                {
                    if (!transferred)
                        owner.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _writer.TryComplete();
            LogStopped(_logger);
        }
    }

    [LoggerMessage(EventId = 100, Level = LogLevel.Information,
        Message = "Listening for BSD syslog on udp://{Address}:{Port}")]
    private static partial void LogListening(ILogger logger, IPAddress address, int port);

    [LoggerMessage(EventId = 101, Level = LogLevel.Warning,
        Message = "UDP receive failed; listener continues")]
    private static partial void LogReceiveFailure(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 102, Level = LogLevel.Information,
        Message = "UDP syslog listener stopped")]
    private static partial void LogStopped(ILogger logger);
}
