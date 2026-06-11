using System.Net;
using System.Net.Sockets;
using LogCollector.Application.Channels;
using LogCollector.Core.Interfaces;
using LogCollector.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LogCollector.Infrastructure.Network;

/// <summary>
/// UDP-сервер для приёма Syslog от MikroTik.
/// UDP проще TCP в плане reassembly — каждый датаграмм уже является целым сообщением.
/// Ключевая оптимизация: переиспользуемый pinned буфер на весь lifetime сервиса.
/// </summary>
public sealed class UdpLogListener : BackgroundService
{
    private readonly LogChannel _channel;
    private readonly ILogParser _parser;
    private readonly ILogger<UdpLogListener> _logger;
    private readonly IPEndPoint _endpoint;

    // Syslog RFC рекомендует до 1024 байт, MikroTik иногда шлёт до 8192. Берём с запасом.
    private const int MaxUdpPacketSize = 65_507;

    public UdpLogListener(
        LogChannel channel,
        ILogParser parser,
        ILogger<UdpLogListener> logger,
        IPEndPoint endpoint)
    {
        _channel  = channel;
        _parser   = parser;
        _logger   = logger;
        _endpoint = endpoint;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var udpClient = new UdpClient(_endpoint);
        _logger.LogInformation("UDP listener started on {Endpoint}", _endpoint);

        // pinned: true — фиксируем массив в памяти навсегда.
        // Обычный new byte[] может быть перемещён GC при compaction,
        // что вызывает проблемы при передаче в нативные сокет-вызовы.
        // Оправдано для буфера с lifetime = lifetime сервиса.
        var buffer = GC.AllocateArray<byte>(MaxUdpPacketSize, pinned: true);
		// Создаем отдельный объект для хранения адреса отправителя
		EndPoint remoteIpEndPoint = new IPEndPoint(IPAddress.Any, 0);
        while (false == ct.IsCancellationRequested)
        {
            try
            {
                // ReceiveFromAsync с Memory<byte> — данные пишутся прямо в наш буфер без копирования
                var result = await udpClient.Client.ReceiveFromAsync(
                    buffer.AsMemory(),
                    SocketFlags.None,
                    _endpoint,
                    ct);

                var remoteEndpoint = (IPEndPoint)result.RemoteEndPoint;

                // Берём только реально полученные байты, не весь буфер
                var data  = buffer.AsMemory(0, result.ReceivedBytes);
                bool isEntry = _parser.TryParse(data, remoteEndpoint, out LogEntry entry);

                if (isEntry)
                    await _channel.Writer.WriteAsync(entry, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UDP receive error");
            }
        }
    }
}
