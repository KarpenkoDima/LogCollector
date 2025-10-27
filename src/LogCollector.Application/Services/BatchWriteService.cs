using LogCollector.Application.Channels;
using LogCollector.Application.Options;
using LogCollector.Core.Interfaces;
using LogCollector.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Threading.Channels;
using System.Xml;

namespace LogCollector.Application.Services;

public sealed class BatchWriteService : BackgroundService
{
    private readonly LogCollectorOptions _logOptions;
    private readonly LogChannel _channel;
    private readonly ILogRepository _repository;
    private readonly ILogger<BatchWriteService> _logger;


    // Linger-еаймер. Один на весь сервис, переиспользуется через TryReset().
    // Доступ - только из потока ExecuteAsync(), поэтому ни volatile ни intelocked не нужны.
    private CancellationTokenSource _lingerCts = null;

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

    // Dispose() намеренно не переопределён: единственный disposable-ресурс
    // (_lingerCts) освобождается в finally ExecuteAsync, гонок с хостом нет.

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Батч аллоцируется один раз и переиспользуется: Clear() сохраняет capacity,
        // внутренний массив не пересоздаётся.
        var batch = new List<LogEntry>(_logOptions.BatchSize);

        _lingerCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        try
        {
            bool channelAlive = true;
            while (channelAlive && false == stoppingToken.IsCancellationRequested)
            {
                channelAlive = await FillBatchAsync(batch, stoppingToken);

                if (batch.Count > 0)
                {
                    await FlushWithRetryAsync(batch, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when(stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown. Недописанный batch остаётся в списке —
            // его дольёт DrainRemainingAsync вместе с остатками канала.
        }
        finally
        {
            _lingerCts.Dispose();
        }

        // Дописываем хвостик из batch плюс то что осталось в channel
        // stoppingToken уже отменён на этот момент
        await DrainRemainingAsync(batch);
    }

    private async Task FlushWithRetryAsync(List<LogEntry> batch, CancellationToken ct)
    {
        try
        {
            // Polly будет тут, пока в репозиторий
            await _repository.InsertBatchAsync(batch, ct);

            batch.Clear();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown посреди записи — это НЕ повод хоронить батч в DLQ.
            // Пробрасываем наверх, batch не очищаем: DrainRemainingAsync
            // сделает финальную попытку со свежим токеном.
            throw;
        }
        catch (Exception ex)
        {
           // Сделаем Logging
           // И DLQ


            batch.Clear();
        }
    }

    private async Task DrainRemainingAsync(List<LogEntry> batch)
    {
        // Синхронно выгребаем всё, что осталось в канале (писатели уже остановлены хостом).
        while (_channel.Reader.TryRead(out var entry))
            batch.Add(entry);

        if (batch.Count == 0)
        {
            return;
        }

        // НЕ линкуемся на stoppingToken — он уже отменён, и linked-токен
        // родился бы «мёртвым»: InsertBatchAsync отменился бы мгновенно,
        // а финальные логи терялись бы при каждом рестарте сервиса.
        // Независимый дедлайн; должен укладываться в HostOptions.ShutdownTimeout.
        using var cts = new CancellationTokenSource(_logOptions.ShutdownFlushTimeout);

        try
        {
            await _repository.InsertBatchAsync(batch, cts.Token);
            // logging тут
        }
        catch (OperationCanceledException)
        {
            // Сделаем Logging
            // И DLQ 
        }
        catch (Exception ex)
        {

            // Сделаем Logging
            // И DLQ;
        }
        finally
        {
            batch.Clear();
        }
    }

    /// <summary>
    /// Собираем батч: блокируется до первого элемента, затем добираем до BatchSize
    /// либо до истечения linger-окна (FlushInterval), отсчитываемого от первого элемента.
    /// </summary>
    /// <param name="batch"></param>
    /// <param name="stoppingToken"></param>
    /// <returns>false - каналл закрыт, продолжать цикл бессмысленно</returns>
    private async Task<bool> FillBatchAsync(List<LogEntry> batch, CancellationToken stoppingToken)
    {
        LogEntry? entry;

        try
        {
            // Ждём первый элемент без таймаута: пустой канал не должен
            //будить сервис вхолостую каждые FlushInterval.
            entry = await _channel.Reader.ReadAsync(stoppingToken);
        }
        catch (ChannelClosedException)
        {
            return false;
        }

        batch.Add(entry);

        // Монотонный дедлайн вместо CancelAfter-на-итерацию:
        // TickCount64 не зависит от перевода системных часов и ничего не аллоцирует.
        var deadline = Environment.TickCount64 + (long)_logOptions.FlushInterval.TotalMilliseconds;

        while (batch.Count < _logOptions.BatchSize)
        {
            // Горячий путь: элементы уже в канале - забираем синхронно, без await
            if (_channel.Reader.TryRead(out entry))
            {
                batch.Add(entry);
                continue;
            }

            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
            {
                break; // linger истёк - отдаём неполный батч
            }

            // Переиспользуем CTS. TryReset() возвращает true, если предыдущий
            // CancelAfter не успел сработать (мы вышли из ожидания по данным) —
            // тогда таймер просто перевзводится без аллокаций.
            // false — таймер сработал или прилетел stoppingToken: пересоздаём.
            if (false == _lingerCts.TryReset())
            {
                _lingerCts.Dispose();
                _lingerCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            }
            _lingerCts.CancelAfter(TimeSpan.FromMilliseconds(remaining));

            try
            {
                // ВАЖНО: false означает «канал completed и пуст».
                // Ранее это не проверялось -> бесконечный spin на 100% CPU.
                if (false == await _channel.Reader.WaitToReadAsync(_lingerCts.Token))
                {
                    return false;
                }
            }
            catch(OperationCanceledException) when (false==stoppingToken.IsCancellationRequested)
            {
                break; // сработал имеено linger-таймер
            }
            // OCE от stoppingToken пробрасывается в ExecuteAsync:
            // там его перехватит фильтрованный catch, batch уцелеет.
            catch (ChannelClosedException)
            {
                return false;
            }
        }

        return true;
    }
}
