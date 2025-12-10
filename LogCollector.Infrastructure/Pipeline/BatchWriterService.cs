using System.Threading.Channels;
using LogCollector.Application.Interfaces;
using LogCollector.Core.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LogCollector.Infrastructure.Pipeline;

/// <summary>
/// A <see cref="BackgroundService"/> that drains <see cref="LogEntry"/> values from the
/// bounded channel and delegates persistence to <see cref="ILogRepository"/>.
///
/// <para>
/// This class knows nothing about SQLite, connection strings, or SQL syntax.
/// Those details live in <c>SqliteLogRepository</c> (Infrastructure.Persistence).
/// Injecting a different <see cref="ILogRepository"/> — for example, an in-memory
/// fake — is sufficient to unit-test all timing and disposal behaviour here
/// without touching a database at all.
/// </para>
///
/// <para><b>Batch-drain pattern (Cleary):</b></para>
/// <para>
/// <c>WaitToReadAsync</c> parks the thread until data arrives or the timeout fires.
/// <c>TryRead</c> in a tight loop then drains up to <c>BatchSize</c> entries
/// synchronously — no round-trips to the scheduler, no per-entry awaits.
/// One <c>SaveBatchAsync</c> call (= one SQLite transaction) covers the whole batch.
/// </para>
/// </summary>
public sealed class BatchWriterService : BackgroundService
{
    private readonly ChannelReader<LogEntry> _reader;
    private readonly ILogRepository _repository;
    private readonly ILogger<BatchWriterService> _logger;
    private readonly BatchWriterOptions _options;

    public BatchWriterService(
        ChannelReader<LogEntry> reader,
        ILogRepository repository,
        IOptions<BatchWriterOptions> options,
        ILogger<BatchWriterService> logger)
    {
        _reader     = reader;
        _repository = repository;
        _logger     = logger;
        _options    = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Delegate schema creation to the repository — BatchWriterService
        // has no business knowing whether the store is SQLite, Postgres, or a file.
        await _repository.InitializeAsync(stoppingToken);

        var batch = new List<LogEntry>(_options.BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            // ── Wait for data (with timeout) ──────────────────────────────────
            using var batchCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            batchCts.CancelAfter(_options.BatchTimeout);

            bool channelCompleted = false;
            try
            {
                bool hasMore = await _reader.WaitToReadAsync(batchCts.Token);
                if (!hasMore) channelCompleted = true;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // BatchTimeout — fall through to flush partial batch.
            }

            // ── Drain synchronously ───────────────────────────────────────────
            while (batch.Count < _options.BatchSize && _reader.TryRead(out var entry))
                batch.Add(entry);

            if (batch.Count > 0)
            {
                await SaveAndDisposeAsync(batch, stoppingToken);
                batch.Clear();
            }

            if (channelCompleted) break;
        }

        // ── Drain remaining entries on shutdown ───────────────────────────────
        _logger.LogInformation("BatchWriterService stopping — draining channel");

        while (_reader.TryRead(out var entry))
        {
            batch.Add(entry);
            if (batch.Count >= _options.BatchSize)
            {
                await SaveAndDisposeAsync(batch, CancellationToken.None);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
            await SaveAndDisposeAsync(batch, CancellationToken.None);

        _logger.LogInformation("BatchWriterService stopped");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Calls <see cref="ILogRepository.SaveBatchAsync"/> and then disposes every
    /// entry's pool buffer in a <c>finally</c> block — regardless of outcome.
    /// </summary>
    private async Task SaveAndDisposeAsync(List<LogEntry> batch, CancellationToken ct)
    {
        try
        {
            await _repository.SaveBatchAsync(batch, ct);
            _logger.LogDebug("Saved {Count} entries", batch.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Repository write failed for batch of {Count} — entries dropped", batch.Count);
        }
        finally
        {
            // Return every pool buffer to ArrayPool regardless of write outcome.
            foreach (var entry in batch)
                entry.RawBuffer?.Dispose();
        }
    }
}
