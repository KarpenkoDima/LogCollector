using System.Diagnostics.Metrics;
using LogCollector.Core.Domain;
using LogCollector.Infrastructure.Diagnostics;
using Xunit;

namespace LogCollector.Tests.Diagnostics;

/// <summary>
/// P2.1 — метрики через System.Diagnostics.Metrics.
///
/// Проверяем, что LogCollectorMetrics корректно записывает значения в
/// инструменты Meter. Слушаем через MeterListener — тот же механизм, что
/// использует OpenTelemetry Prometheus exporter при scrape.
///
/// Gauge (channel_depth, loki_queue_depth, buffer_owners_outstanding)
/// опрашиваются callback'ом при measurement — проверяем, что привязанный
/// источник читается.
/// </summary>
public sealed class MetricsTests : IDisposable
{
    private readonly LogCollectorMetrics _metrics = new();

    public void Dispose() => _metrics.Dispose();

    // ══════════════════════════════════════════════════════════════════════════
    // Инструментация: OwnedIngress вызывает метрики при вытеснении
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void OwnedIngress_Eviction_IncrementsDroppedMetric()
    {
        using var c = new Collector();
        using var metrics = new LogCollector.Infrastructure.Diagnostics.LogCollectorMetrics();

        // Ingress ёмкости 1 с метриками — переполнение на второй записи.
        var ingress = new LogCollector.Infrastructure.Pipeline.OwnedIngress(
            capacity: 1, metrics);

        ingress.TryEnqueue(MakeEntry());   // A
        ingress.TryEnqueue(MakeEntry());   // B вытесняет A → RecordDropped()

        Assert.Equal(1, c.Get("channel_dropped_total"));
    }

    [Fact]
    public void OwnedIngress_ChannelDepth_ReflectsBacklog()
    {
        using var c = new Collector();
        using var metrics = new LogCollector.Infrastructure.Diagnostics.LogCollectorMetrics();

        var ingress = new LogCollector.Infrastructure.Pipeline.OwnedIngress(
            capacity: 10, metrics);

        ingress.TryEnqueue(MakeEntry());
        ingress.TryEnqueue(MakeEntry());
        ingress.TryEnqueue(MakeEntry());

        // channel_depth привязан к Reader.Count — backlog из 3 записей.
        Assert.Equal(3, c.Get("channel_depth"));
    }

    private static LogEntry MakeEntry() => new()
    {
        Priority     = 30,
        TimestampRaw = System.Text.Encoding.UTF8.GetBytes("Jun  4 18:00:00").AsMemory(),
        Hostname     = System.Text.Encoding.UTF8.GetBytes("fw").AsMemory(),
        Topic        = ReadOnlyMemory<byte>.Empty,
        Severity     = SyslogSeverity.Info,
        Message      = System.Text.Encoding.UTF8.GetBytes("x").AsMemory(),
        ReceivedAt   = DateTimeOffset.UtcNow,
        RawBuffer    = null,
    };

    /// <summary>
    /// Собирает значения counter/histogram по имени инструмента.
    /// </summary>
    private sealed class Collector : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly HashSet<string> _observableNames = new();
        private readonly Dictionary<string, double> _sums = new();     // counter/histogram
        private readonly Dictionary<string, double> _latest = new();   // gauge (last value)
        private readonly object _lock = new();

        public Collector()
        {
            _listener.InstrumentPublished = (inst, l) =>
            {
                if (inst.Meter.Name != LogCollectorMetrics.MeterName)
                    return;
                // Помечаем observable-инструменты (gauge) — их значения замещаем,
                // а не суммируем, потому что RecordObservableInstruments можно
                // вызывать многократно.
                if (inst.IsObservable)
                    lock (_lock) _observableNames.Add(inst.Name);
                l.EnableMeasurementEvents(inst);
            };

            _listener.SetMeasurementEventCallback<long>((inst, val, tags, state) =>
                Record(inst.Name, val));
            _listener.SetMeasurementEventCallback<double>((inst, val, tags, state) =>
                Record(inst.Name, val));
            _listener.SetMeasurementEventCallback<int>((inst, val, tags, state) =>
                Record(inst.Name, val));

            _listener.Start();
        }

        private void Record(string name, double val)
        {
            lock (_lock)
            {
                if (_observableNames.Contains(name))
                    _latest[name] = val;                                  // gauge: замещаем
                else
                    _sums[name] = _sums.GetValueOrDefault(name) + val;    // counter: суммируем
            }
        }

        public double Get(string instrument)
        {
            _listener.RecordObservableInstruments();   // опросить gauge заново
            lock (_lock)
            {
                if (_latest.TryGetValue(instrument, out var g)) return g;
                return _sums.GetValueOrDefault(instrument);
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    // ── Counters ──────────────────────────────────────────────────────────────

    [Fact]
    public void RecordDatagram_IncrementsCountAndBytes()
    {
        using var c = new Collector();

        _metrics.RecordDatagram(100);
        _metrics.RecordDatagram(50);

        Assert.Equal(2,   c.Get("udp_datagrams_received_total"));
        Assert.Equal(150, c.Get("udp_bytes_received_total"));
    }

    [Fact]
    public void RecordParseFailure_IncrementsCounter()
    {
        using var c = new Collector();
        _metrics.RecordParseFailure();
        _metrics.RecordParseFailure();
        Assert.Equal(2, c.Get("parse_failures_total"));
    }

    [Fact]
    public void RecordDropped_IncrementsCounter()
    {
        using var c = new Collector();
        _metrics.RecordDropped();
        Assert.Equal(1, c.Get("channel_dropped_total"));
    }

    [Fact]
    public void RecordSqliteWrite_IncrementsBySaveCount()
    {
        using var c = new Collector();
        _metrics.RecordSqliteWrite(8);
        _metrics.RecordSqliteWrite(4);
        Assert.Equal(12, c.Get("sqlite_writes_total"));
    }

    [Fact]
    public void RecordSqliteFailure_And_LokiFailure_Separated()
    {
        using var c = new Collector();
        _metrics.RecordSqliteFailure();
        _metrics.RecordLokiFailure();
        _metrics.RecordLokiFailure();

        Assert.Equal(1, c.Get("sqlite_write_failures_total"));
        Assert.Equal(2, c.Get("loki_push_failures_total"));
    }

    // ── Histograms ────────────────────────────────────────────────────────────

    [Fact]
    public void RecordBatch_RecordsSizeAndDuration()
    {
        using var c = new Collector();
        _metrics.RecordBatch(size: 8, durationMs: 5.5);

        // Histogram суммирует записанные значения в нашем Collector.
        Assert.Equal(8,   c.Get("batch_size"));
        Assert.Equal(5.5, c.Get("batch_duration_ms"));
    }

    // ── Gauges (observable) ───────────────────────────────────────────────────

    [Fact]
    public void ChannelDepth_ReadsBoundSource()
    {
        // metrics создаётся ПОСЛЕ Collector, чтобы listener.InstrumentPublished
        // поймал его инструменты (published-события идут только для инструментов,
        // созданных при активном listener).
        using var c = new Collector();
        using var metrics = new LogCollectorMetrics();

        int depth = 42;
        metrics.BindChannelDepth(() => depth);

        Assert.Equal(42, c.Get("channel_depth"));

        depth = 7;
        Assert.Equal(7, c.Get("channel_depth"));
    }

    [Fact]
    public void BufferOwnersOutstanding_TracksRentMinusReturn()
    {
        using var c = new Collector();
        using var metrics = new LogCollectorMetrics();

        metrics.RentBuffer();
        metrics.RentBuffer();
        metrics.RentBuffer();
        metrics.ReturnBuffer();

        Assert.Equal(2, c.Get("buffer_owners_outstanding"));
    }

    [Fact]
    public void LokiQueueDepth_ReadsBoundSource()
    {
        using var c = new Collector();
        using var metrics = new LogCollectorMetrics();

        int qDepth = 15;
        metrics.BindLokiQueueDepth(() => qDepth);
        Assert.Equal(15, c.Get("loki_queue_depth"));
    }
}
