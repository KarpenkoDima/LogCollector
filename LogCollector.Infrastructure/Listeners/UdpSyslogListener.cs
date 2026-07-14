using System.Buffers;
using System.Net;
using System.Net.Sockets;
using LogCollector.Application.Interfaces;
using LogCollector.Core.Domain;
using LogCollector.Infrastructure.Pipeline;
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
/// a <c>CompositeLogParser</c> in production. Adding a new format requires
/// zero changes here.
/// </para>
///
/// <para><b>Zero-allocation hot path (Kokosa):</b></para>
/// <para>
/// Buffer 1 — <c>GC.AllocateArray(pinned:true)</c>: stable address in Pinned
/// Object Heap for OS DMA. Allocated once, lives for the service lifetime.
/// </para>
/// <para>
/// Buffer 2 — <c>MemoryPool&lt;byte&gt;.Shared.Rent()</c>: per-datagram copy
/// with stable lifetime for <see cref="LogEntry"/> slices. Disposed by
/// <c>BatchWriterService</c> after the batch is written.
/// </para>
/// <para>
/// <c>SocketAddress</c> reuse: the .NET 6+ overload
/// <c>ReceiveFromAsync(Memory&lt;byte&gt;, SocketAddress, CancellationToken)</c>
/// accepts a pre-allocated <see cref="SocketAddress"/> and fills it in-place,
/// eliminating one heap allocation per datagram compared with the legacy
/// <c>EndPoint</c> overload which allocates internally.
/// </para>
/// </summary>
public sealed class UdpSyslogListener : BackgroundService
{
    private readonly byte[] _pinnedReceiveBuffer;
    private readonly IOwnedIngress _ingress;
    private readonly ILogParser _parser;
    private readonly ILogger<UdpSyslogListener> _logger;
    private readonly SyslogListenerOptions _options;

    public UdpSyslogListener(
        IOwnedIngress ingress,
        ILogParser parser,
        IOptions<SyslogListenerOptions> options,
        ILogger<UdpSyslogListener> logger)
    {
        _ingress = ingress;
        _parser  = parser;
        _logger  = logger;
        _options = options.Value;

        // Pinned Object Heap: stable address for OS DMA, no GC relocation.
        // Allocated once in constructor — same lifetime as this service.
        _pinnedReceiveBuffer = GC.AllocateArray<byte>(_options.MaxDatagramSize, pinned: true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var socket = CreateBoundSocket(_options.Port);

        Memory<byte> receiveWindow = _pinnedReceiveBuffer;

        // Pre-allocated SocketAddress for IPv4 (16 bytes).
        // Passed to ReceiveFromAsync on every call — filled in-place by the OS,
        // never replaced. One allocation for the entire service lifetime.
        // Compare: the legacy EndPoint overload allocates a new SocketAddress
        // internally on every call → 10 000 Gen0 objects/sec at 10k pps.
        SocketAddress senderAddress = new SocketAddress(AddressFamily.InterNetwork);

        _logger.LogInformation(
            "UDP listener bound to 0.0.0.0:{Port} (max datagram: {Max} bytes)",
            _options.Port, _options.MaxDatagramSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            int length;
            try
            {
                // ValueTask<int> overload — no Task object allocated in the
                // common case where data is already waiting in the socket buffer.
                // senderAddress is overwritten with the actual sender's address.
                length = await socket
                    .ReceiveFromAsync(receiveWindow, SocketFlags.None, senderAddress, stoppingToken)
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

            IMemoryOwner<byte>? datagramOwner = MemoryPool<byte>.Shared.Rent(length);
            bool ownershipTransferred = false;

            try
            {
                _pinnedReceiveBuffer.AsSpan(0, length).CopyTo(datagramOwner.Memory.Span);

                ReadOnlyMemory<byte> datagram = datagramOwner.Memory[..length];

                // CompositeLogParser — tries each registered format in order.
                if (!_parser.TryParse(datagram, DateTimeOffset.UtcNow, out var entry))
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug(
                            "No parser matched datagram ({Length} B)", length);
                    continue;   // parse failed → finally disposes datagramOwner
                }

                entry = entry with { RawBuffer = datagramOwner };

                // TryEnqueue синхронный и ВСЕГДА принимает новую запись:
                // при переполнении вытесняется СТАРЕЙШАЯ (её буфер диспозится
                // внутри ingress), а наша новая запись входит. Поэтому владение
                // datagramOwner всегда переходит к ingress — Dispose в finally
                // для него не нужен.
                var result = _ingress.TryEnqueue(entry);
                ownershipTransferred = true;

                if (result == EnqueueResult.DroppedOldest && _logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug(
                        "Ingress full — evicted oldest (total dropped: {Dropped})",
                        _ingress.DroppedCount);
            }
            finally
            {
                // Диспозим только если запись НЕ дошла до ingress (parse failure).
                if (!ownershipTransferred)
                    datagramOwner?.Dispose();
            }
        }

        // Сигнализируем ingress: новых записей не будет.
        // Consumer дочитает остаток и увидит завершение канала (часть P0.3).
        _ingress.Complete();

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