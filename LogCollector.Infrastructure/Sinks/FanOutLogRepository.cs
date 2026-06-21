using LogCollector.Application.Interfaces;
using LogCollector.Core.Domain;
using Microsoft.Extensions.Logging;

namespace LogCollector.Infrastructure.Sinks;

/// <summary>
/// Implements <see cref="ILogRepository"/> with a PRIMARY sink and best-effort
/// SECONDARY sinks (P1.1 — isolate SQLite from Loki).
///
/// <para><b>Why primary/secondary instead of Task.WhenAll:</b></para>
/// <para>
/// The old fan-out used <c>Task.WhenAll(all sinks)</c>. That coupled their fates:
/// a hung Loki blocked the whole SaveBatchAsync (so SQLite couldn't take the next
/// batch), and a Loki exception surfaced as an AggregateException that the writer
/// logged as "batch dropped" — even though SQLite had already persisted it.
/// </para>
///
/// <para><b>Contract:</b></para>
/// <list type="number">
///   <item>Primary (SQLite) is awaited first. If it throws, SaveBatchAsync throws —
///         the batch is genuinely lost and the caller must know.</item>
///   <item>Secondaries (Loki, Console) run after a successful primary, with their
///         own timeout. Their failure or hang NEVER fails the batch.</item>
///   <item>Secondary errors are logged by sink name, not propagated.</item>
/// </list>
///
/// <para>
/// Invariant: a successful primary write == the batch is saved, whatever happens
/// to the secondaries.
/// </para>
/// </summary>
public sealed class FanOutLogRepository : ILogRepository, IDisposable
{
    private readonly ILogSink _primary;
    private readonly ILogSink[] _secondaries;
    private readonly TimeSpan _secondaryTimeout;
    private readonly ILogger<FanOutLogRepository> _logger;

    public FanOutLogRepository(
        ILogSink primary,
        ILogSink[] secondaries,
        TimeSpan secondaryTimeout,
        ILogger<FanOutLogRepository> logger)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _secondaries = secondaries ?? Array.Empty<ILogSink>();
        _secondaryTimeout = secondaryTimeout;
        _logger = logger;
    }

    /// <summary>
    /// Initializes primary first (must succeed), then secondaries.
    /// A secondary that fails to initialize is logged but does not abort startup —
    /// it may recover later (e.g. Loki not yet reachable at boot).
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        // Primary must initialize — its failure is fatal to startup.
        await _primary.InitializeAsync(ct);

        foreach (var s in _secondaries)
        {
            try
            {
                await s.InitializeAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "[FanOut] Secondary sink '{Sink}' failed to initialize — continuing",
                    s.Name);
            }
        }
    }

    public async Task SaveBatchAsync(IReadOnlyList<LogEntry> batch, CancellationToken ct)
    {
        // ── 1. PRIMARY (SQLite) — awaited, no timeout ─────────────────────────
        // If this throws, we let it propagate: the batch is genuinely lost and
        // BatchWriterService must log it as a real failure.
        await _primary.SaveBatchAsync(batch, ct);

        // Primary succeeded → the batch IS saved. Everything below is best-effort.
        if (_secondaries.Length == 0)
            return;

        // ── 2. SECONDARIES (Loki, Console) — timeout, errors swallowed ────────
        // Linked to ct so shutdown also cancels secondaries; CancelAfter bounds
        // how long a hung secondary can hold the pipeline.
        using var secondaryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        secondaryCts.CancelAfter(_secondaryTimeout);

        // Run secondaries in parallel — Loki and Console are independent, and a
        // hung Loki must not delay Console. Each is individually guarded so no
        // exception ever escapes into the primary's success path.
        var tasks = new Task[_secondaries.Length];
        for (int i = 0; i < _secondaries.Length; i++)
        {
            var sink = _secondaries[i];
            tasks[i] = RunSecondaryAsync(sink, batch, secondaryCts.Token, ct);
        }

        // WhenAll over already-guarded tasks: none of them throw, so this never
        // throws. It simply waits until all secondaries finish or time out.
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Runs one secondary sink, swallowing timeouts and failures. Distinguishes
    /// a real shutdown (outerCt cancelled) from a secondary timeout (only the
    /// linked token cancelled) — the former is logged as cancellation, the latter
    /// as a best-effort miss. Never throws.
    /// </summary>
    private async Task RunSecondaryAsync(
        ILogSink sink,
        IReadOnlyList<LogEntry> batch,
        CancellationToken secondaryToken,
        CancellationToken outerCt)
    {
        try
        {
            await sink.SaveBatchAsync(batch, secondaryToken);
        }
        catch (OperationCanceledException) when (outerCt.IsCancellationRequested)
        {
            // Shutdown in progress — expected, not an error.
            _logger.LogDebug(
                "[FanOut] Secondary '{Sink}' cancelled by shutdown", sink.Name);
        }
        catch (OperationCanceledException)
        {
            // Secondary timeout — best-effort miss. Batch already saved by primary.
            _logger.LogWarning(
                "[FanOut] Secondary '{Sink}' timed out after {Timeout}ms — " +
                "batch already persisted by primary, skipping",
                sink.Name, _secondaryTimeout.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            // Any other secondary failure — logged by name, never fails the batch.
            _logger.LogError(ex,
                "[FanOut] Secondary '{Sink}' failed — batch already persisted by " +
                "primary, continuing", sink.Name);
        }
    }

    public void Dispose()
    {
        (_primary as IDisposable)?.Dispose();
        foreach (var s in _secondaries)
            (s as IDisposable)?.Dispose();
    }
}
