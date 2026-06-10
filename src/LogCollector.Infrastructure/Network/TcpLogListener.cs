using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using LogCollector.Application.Channels;
using LogCollector.Core.Interfaces;
using LogCollector.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LogCollector.Infrastructure.Network;

/// <summary>
/// TCP-сервер для приёма логов от Winlogbeat.
/// Использует System.IO.Pipelines для:
/// 1. Zero-copy чтения из сокета (нет new byte[] на каждый пакет).
/// 2. Автоматического reassembly фрагментированных сообщений через AdvanceTo.
/// </summary>
public sealed class TcpLogListener : BackgroundService
{
    private readonly LogChannel _channel;
    private readonly ILogParser _parser;
    private readonly ILogger<TcpLogListener> _logger;
    private readonly IPEndPoint _endpoint;

    public TcpLogListener(
        LogChannel channel,
        ILogParser parser,
        ILogger<TcpLogListener> logger,
        IPEndPoint endpoint)
    {
        _channel  = channel;
        _parser   = parser;
        _logger   = logger;
        _endpoint = endpoint;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var listener = new TcpListener(_endpoint);
        listener.Start();
        _logger.LogInformation("TCP listener started on {Endpoint}", _endpoint);

        while (false == ct.IsCancellationRequested)
        {
            var client = await listener.AcceptTcpClientAsync(ct);

            // Намеренно не await-им — не блокируем приём новых соединений.
            // Каждое соединение живёт в своей Task.
            _ = HandleConnectionAsync(client, ct);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        var remoteEndpoint = (IPEndPoint)client.Client.RemoteEndPoint!;
        _logger.LogDebug("Connection from {Remote}", remoteEndpoint);

        using (client)
        {
            var reader = PipeReader.Create(client.GetStream());
            try
            {
                await ProcessPipeAsync(reader, remoteEndpoint, ct);
            }
            finally
            {
                await reader.CompleteAsync();
            }
        }
    }

    private async Task ProcessPipeAsync(
        PipeReader reader,
        IPEndPoint remote,
        CancellationToken ct)
    {
        while (false == ct.IsCancellationRequested)
        {
            // Возвращает управление когда в буфере появятся ЛЮБЫЕ данные
            var result = await reader.ReadAsync(ct);
            var buffer = result.Buffer; // ReadOnlySequence<byte> — цепочка сегментов

            var consumed = await ProcessBufferAsync(buffer, remote, ct);

            // consumed — до сюда память освобождается
            // buffer.End — до сюда мы "смотрели" (examined)
            // Если consumed < buffer.End, PipeReader сохранит остаток
            // и допишет к нему следующую порцию — автоматический reassembly
            reader.AdvanceTo(consumed, buffer.End);

            if (result.IsCompleted) break;
        }
    }

    private async Task<SequencePosition> ProcessBufferAsync(
    ReadOnlySequence<byte> buffer,
    IPEndPoint remote,
    CancellationToken ct)
    {
        // Шаг 1: синхронно парсим всё что есть в буфере — ref struct живёт только здесь
        var parsed = new List<LogEntry>();
        SequencePosition consumed;

        {
            var reader = new SequenceReader<byte>(buffer);

            while (reader.TryReadTo(out ReadOnlySequence<byte> line, (byte)'\n'))
            {
                if (line.IsEmpty) continue;

                var data = line.IsSingleSegment
                    ? line.First
                    : new ReadOnlyMemory<byte>(line.ToArray());

                var entry = _parser.TryParse(data, remote);
                if (entry is not null)
                    parsed.Add(entry);
            }

            consumed = reader.Position;
        } // ← reader уничтожается здесь, до любого await

        // Шаг 2: теперь можно await — ref struct уже вне scope
        foreach (var entry in parsed)
            await _channel.Writer.WriteAsync(entry, ct);

        return consumed;
    }
}
