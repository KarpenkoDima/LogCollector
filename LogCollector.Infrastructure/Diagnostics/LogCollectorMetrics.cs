using System.Diagnostics.Metrics;

namespace LogCollector.Infrastructure.Diagnostics;

/// <summary>
/// Central owner of the LogCollector <see cref="Meter"/> and all P2.1 metrics.
///
/// <para>
/// Registered as a singleton. Every component (listener, ingress, batch writer,
/// sinks) injects this and calls typed methods — so instrument names, types, and
/// units live in exactly one place.
/// </para>
///
/// <para>
/// Pure <see cref="System.Diagnostics.Metrics"/> — no OpenTelemetry or Prometheus
/// dependency here. Export to Prometheus is wired up separately in the Host layer,
/// which keeps Infrastructure unaware of the exporter.
/// </para>
///
/// <para><b>Instrument types (audit P2.1):</b></para>
/// <list type="bullet">
///   <item>Counter — monotonic totals (datagrams, bytes, failures, writes, drops).</item>
///   <item>Histogram — distributions (batch size, batch duration).</item>
///   <item>ObservableGauge — point-in-time values polled at scrape
///         (channel depth, loki queue depth, outstanding buffer owners).</item>
/// </list>
/// </summary>
public sealed class LogCollectorMetrics : IDisposable
{
    public const string MeterName = "LogCollector";

    private readonly Meter _meter;

    // ── Counters ──────────────────────────────────────────────────────────────
    private readonly Counter<long> _datagrams;
    private readonly Counter<long> _bytes;
    private readonly Counter<long> _parseFailures;
    private readonly Counter<long> _dropped;
    private readonly Counter<long> _sqliteWrites;
    private readonly Counter<long> _sqliteFailures;
    private readonly Counter<long> _lokiFailures;

    // ── Histograms ────────────────────────────────────────────────────────────
    private readonly Histogram<long>   _batchSize;
    private readonly Histogram<double> _batchDuration;

    // ── Gauge state ───────────────────────────────────────────────────────────
    // buffer_owners_outstanding = rented - returned (Interlocked for thread safety).
    private long _buffersRented;
    private long _buffersReturned;

    // channel_depth / loki_queue_depth are read from bound callbacks at scrape.
    // Volatile so the gauge callback (any thread) sees the latest delegate.
    private volatile Func<int>? _channelDepthSource;
    private volatile Func<int>? _lokiQueueDepthSource;

    public LogCollectorMetrics()
    {
        _meter = new Meter(MeterName);

        _datagrams = _meter.CreateCounter<long>(
            "udp_datagrams_received_total", description: "Datagrams received from socket");
        _bytes = _meter.CreateCounter<long>(
            "udp_bytes_received_total", unit: "bytes", description: "Input volume");
        _parseFailures = _meter.CreateCounter<long>(
            "parse_failures_total", description: "Unrecognised datagrams");
        _dropped = _meter.CreateCounter<long>(
            "channel_dropped_total", description: "Entries evicted by drop-oldest");
        _sqliteWrites = _meter.CreateCounter<long>(
            "sqlite_writes_total", description: "Rows written by primary sink");
        _sqliteFailures = _meter.CreateCounter<long>(
            "sqlite_write_failures_total", description: "Primary sink failures");
        _lokiFailures = _meter.CreateCounter<long>(
            "loki_push_failures_total", description: "Secondary sink failures");

        _batchSize = _meter.CreateHistogram<long>(
            "batch_size", description: "Actual batch sizes");
        _batchDuration = _meter.CreateHistogram<double>(
            "batch_duration_ms", unit: "ms", description: "Batch processing time");

        // Observable gauges — polled at scrape via callbacks.
        // All observable gauges are explicitly long-typed so a single
        // long measurement callback (used by exporters and tests) sees them all.
        _meter.CreateObservableGauge(
            "channel_depth",
            () => (long)(_channelDepthSource?.Invoke() ?? 0),
            description: "Current channel backlog");

        _meter.CreateObservableGauge(
            "loki_queue_depth",
            () => (long)(_lokiQueueDepthSource?.Invoke() ?? 0),
            description: "Loki secondary backlog");

        _meter.CreateObservableGauge<long>(
            "buffer_owners_outstanding",
            () => Interlocked.Read(ref _buffersRented) - Interlocked.Read(ref _buffersReturned),
            description: "Pool buffers rented but not yet returned (should trend to 0)");
    }

    // ── Counter recording ─────────────────────────────────────────────────────

    public void RecordDatagram(int bytes)
    {
        _datagrams.Add(1);
        _bytes.Add(bytes);
    }

    public void RecordParseFailure() => _parseFailures.Add(1);
    public void RecordDropped()      => _dropped.Add(1);
    public void RecordSqliteWrite(int count) => _sqliteWrites.Add(count);
    public void RecordSqliteFailure() => _sqliteFailures.Add(1);
    public void RecordLokiFailure()   => _lokiFailures.Add(1);

    // ── Histogram recording ───────────────────────────────────────────────────

    public void RecordBatch(int size, double durationMs)
    {
        _batchSize.Record(size);
        _batchDuration.Record(durationMs);
    }

    // ── Buffer lifetime tracking (buffer_owners_outstanding gauge) ────────────

    public void RentBuffer()   => Interlocked.Increment(ref _buffersRented);
    public void ReturnBuffer() => Interlocked.Increment(ref _buffersReturned);

    // ── Gauge source binding ──────────────────────────────────────────────────
    // Called once during startup wiring so the gauge callback can read the live
    // source (e.g. OwnedIngress.Reader.Count) at scrape time.

    public void BindChannelDepth(Func<int> source)   => _channelDepthSource = source;
    public void BindLokiQueueDepth(Func<int> source) => _lokiQueueDepthSource = source;

    public void Dispose() => _meter.Dispose();
}
