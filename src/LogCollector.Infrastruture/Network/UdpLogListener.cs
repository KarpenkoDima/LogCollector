using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using LogCollector.Application.Channels;
using LogCollector.Core.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LogCollector.Infrastruture.Network;

public class UdpLogListener : BackgroundService
{
    private readonly LogChannel _channel;
    private readonly ILogParser _parser;
    private readonly ILogger<UdpLogListener> _logger;
    private readonly IPEndPoint _endPoint;

    // Максимальный размер UDp-пакета на практике.
    // Syslog RFC рекомендует не более 1024 байт, но MikroTik
    // иношда шлёи до 8192. Берём с запасом.
    private const int MaxUdpPackerSize = 65_507;
    
    public UdpLogListener(LogChannel channel, ILogParser parser, ILogger<UdpLogListener> logger, IPEndPoint endPoint)
    {
        _channel = channel;
        _parser = parser;
        _logger = logger;
        _endPoint = endPoint;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var udpClient = new UdpClient(_endPoint);
        _logger.LogInformation("UDP listener started on {Endpoint}", _endPoint);
        
        // Переиспользуемый буфер - аллоцируем один раз на весь lifetime сервиса.
        // Для UDP то безопасно: каждый ReceiveAsync полностью заполняет
        // буфер данными одного пакеты, прежде чем мы его обработаем.
        var buffer = GC.AllocateArray<byte>(MaxUdpPackerSize, pinned: true);

        while (false == stoppingToken.IsCancellationRequested)
        {
            try
            {
                // ReceiveFromAsync с Memory<byte> - zero-copy путь:
                // данные пишутся прямо в наш переиспользуемый буфер
                var result = await udpClient.Client.ReceiveFromAsync(
                    buffer.AsMemory(),
                    SocketFlags.None,
                    _endPoint,
                    stoppingToken
                );

                var remoteEndpoint = (IPEndPoint)result.RemoteEndPoint;

                // Берём только реально полученные байты - не весь буфер
                var data = buffer.AsMemory(0, result.ReceivedBytes);

                var entry = _parser.TryParse(data, remoteEndpoint);

                if (entry is not null)
                {
                    _channel.Writer.WriteAsync(entry, stoppingToken);
                }
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
