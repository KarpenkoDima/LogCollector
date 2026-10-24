using System.Diagnostics;
using System.Threading.Channels;
using LogCollector.Application.Interfaces;
using LogCollector.Core.Domain;
using Microsoft.Extensions.Logging;

namespace LogCollector.Application.Pipeline;

/// <summary>
/// Collects channel items into size- or time-bounded batches. Only explicitly classified
/// transient persistence failures are retried. On cancellation, a separate bounded deadline
/// is used to flush the current batch and the remaining channel contents.
/// </summary>
public sealed partial class BatchProcessor
{
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private readonly ILogRepository _repository;
    private readonly BatchProcessorOptions _options;
    private readonly ILogger<BatchProcessor> _logger;

    public BatchProcessor(
        ILogRepository repository,
        BatchProcessorOptions options,
        ILogger<BatchProcessor> logger)
    {
        _repository = repository;
        _options = options;
        _logger = logger;
    }

    public async Task RunAsync(ChannelReader<LogEntry> reader, CancellationToken cancellationToken)
    {
        await _repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var batch = new List<LogEntry>(_options.BatchSize);
        bool cancelledForShutdown = false;

        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!reader.TryRead(out LogEntry first))
                    continue;

                batch.Add(first);
                long started = Stopwatch.GetTimestamp();

                while (batch.Count < _options.BatchSize)
                {
                    while (batch.Count < _options.BatchSize && reader.TryRead(out LogEntry entry))
                        batch.Add(entry);

                    if (batch.Count >= _options.BatchSize)
                        break;

                    TimeSpan remaining = _options.FlushInterval - Stopwatch.GetElapsedTime(started);
                    if (remaining <= TimeSpan.Zero)
                        break;

                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(remaining);
                    try
                    {
                        if (!await reader.WaitToReadAsync(timeout.Token).ConfigureAwait(false))
                            break;
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                }

                await SaveWithRetryAsync(batch, cancellationToken).ConfigureAwait(false);
                DisposeEntries(batch);
                batch.Clear();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelledForShutdown = true;
            LogShutdownStarted(_logger);
        }
        finally
        {
            if (cancelledForShutdown || cancellationToken.IsCancellationRequested)
            {
                if (!cancelledForShutdown)
                {
                    LogShutdownStarted(_logger);
                }

                await FlushPendingOnShutdownAsync(batch, reader)
                    .ConfigureAwait(false);
            }
            else
            {
                // A fatal persistence error must not silently leak pooled buffers. The host
                // observes the original exception and can restart the process; unpersisted
                // entries are reported explicitly rather than retried forever.
                int dropped = batch.Count;
                DisposeEntries(batch);
                batch.Clear();

                while (reader.TryRead(out LogEntry entry))
                {
                    entry.BufferOwner?.Dispose();
                    dropped++;
                }

                if (dropped > 0)
                    LogBufferedEntriesDropped(_logger, dropped);
            }
        }
    }

    private async Task SaveWithRetryAsync(
        List<LogEntry> batch,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await _repository.SaveBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TransientLogPersistenceException exception) when (attempt < _options.MaxAttempts)
            {
                TimeSpan delay = GetRetryDelay(attempt);
                LogTransientFailure(_logger, attempt, _options.MaxAttempts, delay, exception);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (TransientLogPersistenceException exception)
            {
                LogRetriesExhausted(_logger, attempt, batch.Count, exception);
                throw;
            }
        }
    }

    private async Task FlushPendingOnShutdownAsync(
        List<LogEntry> batch,
        ChannelReader<LogEntry> reader)
    {
        while (reader.TryRead(out LogEntry entry))
            batch.Add(entry);

        if (batch.Count == 0)
            return;

        int pendingCount = batch.Count;
        bool saved = false;
        using var shutdownDeadline = new CancellationTokenSource(_options.ShutdownFlushTimeout);

        try
        {
            await SaveWithRetryAsync(batch, shutdownDeadline.Token).ConfigureAwait(false);
            saved = true;
            LogShutdownFlushSucceeded(_logger, pendingCount);
        }
        catch (OperationCanceledException exception) when (shutdownDeadline.IsCancellationRequested)
        {
            LogShutdownFlushFailed(_logger, pendingCount, _options.ShutdownFlushTimeout, exception);
        }
        catch (Exception exception)
        {
            LogShutdownFlushFailed(_logger, pendingCount, _options.ShutdownFlushTimeout, exception);
        }
        finally
        {
            DisposeEntries(batch);
            batch.Clear();

            int dropped = saved ? 0 : pendingCount;
            while (reader.TryRead(out LogEntry entry))
            {
                entry.BufferOwner?.Dispose();
                dropped++;
            }

            if (dropped > 0)
                LogShutdownEntriesDropped(_logger, dropped);
        }
    }

    private TimeSpan GetRetryDelay(int attempt)
    {
        // attempt is one-based. Cap the exponential multiplier and absolute delay to avoid
        // overflow or accidentally sleeping for hours if configuration is mis-set.
        int exponent = Math.Min(attempt - 1, 5);
        double milliseconds = Math.Min(_options.RetryDelay.TotalMilliseconds * (1 << exponent),
            MaxRetryDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static void DisposeEntries(IEnumerable<LogEntry> entries)
    {
        foreach (LogEntry entry in entries)
            entry.BufferOwner?.Dispose();
    }

    [LoggerMessage(EventId = 300, Level = LogLevel.Information,
        Message = "Cancellation received; flushing buffered log entries before shutdown")]
    private static partial void LogShutdownStarted(ILogger logger);

    [LoggerMessage(EventId = 301, Level = LogLevel.Warning,
        Message = "Transient log persistence failure on attempt {Attempt}/{MaxAttempts}; retrying in {Delay}")]
    private static partial void LogTransientFailure(
        ILogger logger, int attempt, int maxAttempts, TimeSpan delay, Exception exception);

    [LoggerMessage(EventId = 302, Level = LogLevel.Critical,
        Message = "Log persistence failed after {Attempts} attempts for batch of {EntryCount} entries")]
    private static partial void LogRetriesExhausted(
        ILogger logger, int attempts, int entryCount, Exception exception);

    [LoggerMessage(EventId = 303, Level = LogLevel.Information,
        Message = "Shutdown flush persisted {EntryCount} buffered log entries")]
    private static partial void LogShutdownFlushSucceeded(ILogger logger, int entryCount);

    [LoggerMessage(EventId = 304, Level = LogLevel.Critical,
        Message = "Unable to flush {EntryCount} buffered log entries within shutdown deadline {Timeout}")]
    private static partial void LogShutdownFlushFailed(
        ILogger logger, int entryCount, TimeSpan timeout, Exception exception);

    [LoggerMessage(EventId = 305, Level = LogLevel.Critical,
        Message = "Dropping {EntryCount} buffered log entries after an unexpected pipeline exit")]
    private static partial void LogBufferedEntriesDropped(ILogger logger, int entryCount);

    [LoggerMessage(EventId = 306, Level = LogLevel.Critical,
        Message = "Dropped {EntryCount} log entries because shutdown flush did not complete")]
    private static partial void LogShutdownEntriesDropped(ILogger logger, int entryCount);
}
