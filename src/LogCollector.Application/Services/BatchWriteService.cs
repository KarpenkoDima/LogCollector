using LogCollector.Application.Channels;
using LogCollector.Application.Options;
using LogCollector.Core.Interfaces;
using LogCollector.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Threading.Channels;

namespace LogCollector.Application.Services;

public sealed class BatchWriteService : BackgroundService
{
    private readonly LogCollectorOptions _logOptions;
    private readonly LogChannel _channel;
    private readonly ILogRepository _repository;
    private readonly ILogger<BatchWriteService> _logger;

    public BatchWriteService(
        LogChannel channel, 
        ILogRepository repository, 
        ILogger<BatchWriteService> logger,
        IOptions<LogCollectorOptions> options)
    {
        _channel = channel;
        _repository = repository;
        _logger = logger;       
        _logOptions = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // убираем Allocation on GC
        var batch = new List<LogEntry>(_logOptions.BatchSize);

        while (false == stoppingToken.IsCancellationRequested)
        {
            // ❌ batch.Clear() ЗДЕСЬ ОЧИЩАТЬ  НЕЛЬЗЯ из-за риска дублирования при выходе из цикла   
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        cts.CancelAfter(TimeSpan.FromSeconds(_logOptions.FlushInterval));
            try
            {
                await FillBatchAsync(batch, cts.Token);

                if (batch.Count == 0)
                {
                    continue;
                }
                // FIX: Pass stoppingToken, but does not cts.Token!
                var written = await _repository.InsertBatchAsync(batch, stoppingToken);
                _logger.LogDebug("Flushed {Count} log entries to DB", written);
                // ✅ ОЧИЩАЕМ ЗДЕСЬ! 
                // Если запись прошла успешно, список нам больше не нужен.
                // Если же выключение произойдет после этой строки, в DrainRemainingAsync уйдет пустой список.
                batch.Clear();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Batch write failed, entries will be lost");
                batch.Clear();
            }
          /*  finally
            {
                // TODO:  Реализуйте политику повторных попыток (Retry Policy)
                // или механизм Dead Letter Queue.    
                batch.Clear();
            }*/
        }

        // Дописываем хвостик из batch плюс то что осталось в channel
        await DrainRemainingAsync(batch, stoppingToken);
    }

    private async Task DrainRemainingAsync(List<LogEntry> batch, CancellationToken stoppingToken)
    {
        while (_channel.Reader.TryRead(out var entry))
        {
            batch.Add(entry);
        }

        if (batch.Count > 0)
        {
            // CancellationToken.None — намеренно: stoppingToken уже отменён,
            // но финальный flush мы обязаны довести до конца.
            await _repository.InsertBatchAsync(batch, CancellationToken.None);
            _logger.LogInformation("Final flush: {Count} entries", batch.Count);
        }
    }

    private async Task FillBatchAsync(List<LogEntry> batch, CancellationToken stoppingToken)
    {
        try
        {
            // 1. Initial wait: Don't start a batch until there is at least one item.
            var firstEntry = await _channel.Reader.ReadAsync(stoppingToken);
            batch.Add(firstEntry);

            // 2. Fill the rest of the batch greedily
            while (batch.Count < _logOptions.BatchSize)
            {
                // Try to pull items that are already sitting in the buffer
                if (_channel.Reader.TryRead(out var entry))
                {
                    batch.Add(entry);
                }
                else
                {
                    // Buffer is empty. Wait for more, or exit if the Channel is closed.
                    if (false == await _channel.Reader.WaitToReadAsync(stoppingToken))
                    {
                        // Channel was marked as Complete and is empty
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown behavior; the batch gathered so far is still in the 'batch' list
        }
        catch(ChannelClosedException)
        {
            // Channel was closed while we were reading
        }
    }
}
