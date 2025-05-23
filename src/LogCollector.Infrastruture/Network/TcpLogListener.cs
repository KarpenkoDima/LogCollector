using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using LogCollector.Application.Channels;
using LogCollector.Core.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LogCollector.Infrastruture.Network;

public sealed class TcpLogListener : BackgroundService
{
    private readonly LogChannel _channel;
    private readonly ILogParser _parser;
    private readonly ILogger<TcpLogListener> _logger;
    private readonly IPEndPoint _endPoint;

    public TcpLogListener(LogChannel channel, ILogParser parser, ILogger<TcpLogListener> logger, IPEndPoint endPoint)
    {
        _channel = channel;
        _parser = parser;
        _logger = logger;
        _endPoint = endPoint;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var listener = new TcpListener(_endPoint);
        listener.Start();
        _logger.LogInformation("TCP listener started on {EndPoint}", _endPoint);

        while (false == stoppingToken.IsCancellationRequested)
        {
            // AcceptTcpClientAsync возвращает управление как только
            // появляется новое входящее соединение
            var client = await listener.AcceptTcpClientAsync(stoppingToken);

            // Каждое соединение обрабатывается в отдельной Task
            // Намеренно НЕ await-им - не блокируем приём новых соединений.
            _ = HandleConnectionAsync(client, stoppingToken);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken stoppingToken)
    {
        var remoteEndpoint = (IPEndPoint)client.Client.RemoteEndPoint!;
        _logger.LogDebug("Connection from {Remote}", remoteEndpoint);

        using (client)
        {
            // PipeReader оборачивает Stream и берёт на себя
            // управление буферами и reassembly фрагментов
            var reader = PipeReader.Create(client.GetStream());
            try
            {
                await PropcessPipeAsync(reader, remoteEndpoint, stoppingToken);
            }
            finally
            {
                await reader.CompleteAsync();
            }
        }
    }

    private async Task PropcessPipeAsync(PipeReader reader, IPEndPoint? remoteEndpoint, CancellationToken stoppingToken)
    {
        while(false == stoppingToken.IsCancellationRequested)
        {
            // ReadAsync возвращает упрвление когда в буфере
            // появляется ЛЮБЫЕ данные - даже один байт
            var result = await reader.ReadAsync(stoppingToken);
            var buffer = result.Buffer; // ReadOnlySequence<bye> - цепочка еггментов
            
            // Ищем все полные сообщения в текущем буфере
            var consumed = ProcessBuffer(buffer, remoteEndpoint);
            
            // Критически важный вызов:
            // consumed - до сюда обработаны, можно освободить память
            // buffer.End - до сюда мы "посмотрели" (examined)
            // Если consumed < buffer.End, PipeReader сохранит остаток
            // и допишет к нему следующую порцию - єто и есть автоматический reassembly
            reader.AdvanceTo(consumed, buffer.End);

            if (result.IsCompleted)
            {
                break; // клиент закрыл соединение.
            }
        }
    }

    private SequencePosition ProcessBuffer(
        ReadOnlySequence<byte> buffer, 
        IPEndPoint? remoteEndpoint)
    {
        var reader = new SequenceReader<byte>(buffer);
        
        // TryReadTo ищет разделитель \n и возвращает span до него -
        // без единой аллокации, работая прямо с буферами PipeReader
        while (reader.TryReadTo(out ReadOnlySequence<byte> line, (byte)'\n'))
        {
            if (line.IsEmpty)
            {
                continue;
            }
            
            // Передаём в парсер как Memory - парсер сам решит
            // когда и что декорировать в string
            var entry = _parser.TryParse(
                line.IsSingleSegment
                    ? line.First      // zero-copy если данные в одном сегмент
                    : new ReadOnlyMemory<byte>(line.ToArray()), // ВЫНУЖДЕНО: копирование только при фрагментации
                remoteEndpoint);

            if (entry is not null)
            {
                // TryWrite не делает await - если канал полон,
                // мы молча дропаем. Для Wait-стратегии используем WriteAsync
                _channel.Writer.TryWrite(entry);
            }
        }
        
        // Возвращаем позицию до которой успешно обработали
        return reader.Position;
    }
}