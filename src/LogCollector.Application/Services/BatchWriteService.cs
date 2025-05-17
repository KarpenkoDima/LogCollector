using LogCollector.Application.Channels;
using LogCollector.Core.Interfaces;
using LogCollector.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LogCollector.Application.Services;

public sealed class BatchWriteService : BackgroundService
{
    private readonly LogChannel _channel;
    private readonly ILogRepository _repository;
    private readonly ILogger<BatchWriteService> _logger;

    private const int BatchSize = 500;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);
    public BatchWriteService(LogChannel channel, ILogRepository repository, ILogger<BatchWriteService> logger)
    {
        _channel = channel;
        _repository = repository;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<LogEntry>(BatchSize);

        while (false == stoppingToken.IsCancellationRequested)
        {
            try
            {
                await FillBatchAsync(batch, stoppingToken);

                if (batch.Count == 0)
                {
                    continue;
                }

                var written = await _repository.InsertBatchAsync(batch, stoppingToken);
                _logger.LogDebug("Flushed {Count} log entries to DB", written);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Batch write failed, entries will be lost");
            }
            finally
            {
                batch.Clear();
            }
        }

        await DrainRemainingAsync(batch, stoppingToken);
    }

    private async Task DrainRemainingAsync(List<LogEntry> batch, CancellationToken stoppingToken)
    {
        while (_channel.Reader.TryRead(out var enrty))
        {
            batch.Add(enrty);
        }

        if (batch.Count > 0)
        {
            await _repository.InsertBatchAsync(batch, CancellationToken.None);
            _logger.LogInformation("Final flush: {Count} entries", batch.Count);
        }
    }

    private async Task FillBatchAsync(List<LogEntry> batch, CancellationToken stoppingToken)
    {
        var entry = await _channel.Reader.ReadAsync(stoppingToken);
        batch.Add(entry);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, CancellationToken.None);
        cts.CancelAfter(FlushInterval);

        while(batch.Count < BatchSize && 
            false == cts.Token.IsCancellationRequested)
        {
            if (false == _channel.Reader.TryRead(out entry))
            {
                try
                {
                    await _channel.Reader.WaitToReadAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            else
            {
                batch.Add(entry);
            }
        }
    }
}
