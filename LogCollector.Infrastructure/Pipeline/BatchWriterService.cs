using System.Threading.Channels;
using LogCollector.Application.Interfaces;
using LogCollector.Core.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LogCollector.Infrastructure.Pipeline;

/// <summary>
/// A <see cref="BackgroundService"/> that accumulates <see cref="LogEntry"/> values
/// from the bounded channel into batches and delegates persistence to
/// <see cref="ILogRepository"/>.
///
/// <para>
/// This class knows nothing about SQLite, connection strings, or SQL syntax.
/// Injecting a different <see cref="ILogRepository"/> — for example, an in-memory
/// fake — is sufficient to unit-test all timing and disposal behaviour here
/// without touching a database.
/// </para>
///
/// <para><b>Windowed accumulation (P0.2 fix):</b></para>
/// <para>
/// A batch is a WINDOW that opens on the first entry and closes on the FIRST of:
/// </para>
/// <list type="bullet">
///   <item>batch reaches <c>BatchSize</c> — flush immediately;</item>
///   <item><c>BatchTimeout</c> elapses since the first entry — flush partial;</item>
///   <item>the channel completes (producer done) — flush remainder, exit;</item>
///   <item>shutdown is requested — break to the final drain.</item>
/// </list>
/// <para>
/// The earlier implementation drained only the channel SNAPSHOT at wake-up
/// time. On real (non-burst) traffic that produced batches of size 1
/// (one entry arrives, wake, drain-one, flush, repeat). Windowed accumulation
/// waits for more entries within the timeout, so 20 entries at BatchSize=8
/// produce batches of [8, 8, 4] instead of twenty batches of [1].
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
        await _repository.InitializeAsync(stoppingToken);

        var batch = new List<LogEntry>(_options.BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            // ── 1. Wait for the FIRST entry of a new window ───────────────────
            // Only shutdown interrupts this wait — no timeout yet, because the
            // window (and its timer) starts when the first entry actually arrives.
            bool hasData;
            try
            {
                hasData = await _reader.WaitToReadAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            if (!hasData)
                break;   // channel completed (producer done) — go to final drain

            // ── 2. Open the window: timer starts NOW, on the first entry ──────
            // Linked to stoppingToken so shutdown also cancels the window wait.
            using var windowCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            windowCts.CancelAfter(_options.BatchTimeout);

            bool channelCompleted = false;

            // ── 3. Accumulate until BatchSize OR timeout OR completion ────────
            try
            {
                while (batch.Count < _options.BatchSize)
                {
                    // Drain everything available right now, synchronously.
                    while (batch.Count < _options.BatchSize && _reader.TryRead(out var entry))
                        batch.Add(entry);

                    if (batch.Count >= _options.BatchSize)
                        break;   // window closed by size

                    // Not full yet — wait for the next entry WITHIN the window.
                    // windowCts fires either on BatchTimeout or on shutdown.
                    if (!await _reader.WaitToReadAsync(windowCts.Token))
                    {
                        channelCompleted = true;   // producer completed mid-window
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutdown during the window — flush what we have, then break.
                if (batch.Count > 0)
                    await SaveAndDisposeAsync(batch, CancellationToken.None);
                batch.Clear();
                break;
            }
            catch (OperationCanceledException)
            {
                // BatchTimeout elapsed — window closed by time. Flush partial.
                // (windowCts fired but stoppingToken did NOT — normal timeout.)
            }

            // ── 4. Flush the window (closed by size, timeout, or completion) ──
            if (batch.Count > 0)
            {
                await SaveAndDisposeAsync(batch, stoppingToken);
                batch.Clear();
            }

            if (channelCompleted)
                break;
        }

        // ── 5. Final drain on shutdown ────────────────────────────────────────
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
            foreach (var entry in batch)
                entry.RawBuffer?.Dispose();
        }
    }
}