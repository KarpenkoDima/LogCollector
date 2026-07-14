using System.Diagnostics;
using System.Threading.Channels;
using LogCollector.Application.Interfaces;
using LogCollector.Core.Domain;

namespace LogCollector.Application.Pipeline;

/// <summary>
/// Collects channel items into size- or time-bounded batches. A failed database write
/// is retried while the bounded channel applies backpressure to the network listener.
/// </summary>
public sealed class BatchProcessor
{
    private readonly ILogRepository _repository;
    private readonly BatchProcessorOptions _options;

    public BatchProcessor(ILogRepository repository, BatchProcessorOptions options)
    {
        _repository = repository;
        _options = options;
    }

    public async Task RunAsync(ChannelReader<LogEntry> reader, CancellationToken cancellationToken)
    {
        await _repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var batch = new List<LogEntry>(_options.BatchSize);

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
        finally
        {
            DisposeEntries(batch);
            while (reader.TryRead(out LogEntry entry))
                entry.BufferOwner?.Dispose();
        }
    }

    private async Task SaveWithRetryAsync(
        IReadOnlyList<LogEntry> batch,
        CancellationToken cancellationToken)
    {
        while (true)
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
            catch
            {
                await Task.Delay(_options.RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void DisposeEntries(IEnumerable<LogEntry> entries)
    {
        foreach (LogEntry entry in entries)
            entry.BufferOwner?.Dispose();
    }
}
