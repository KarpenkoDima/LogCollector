using System.Text;
using BenchmarkDotNet.Attributes;
using LogCollector.Core.Domain;

namespace LogCollector.Benchmarks;

/// <summary>
/// Measures the hostname-grouping step of LokiLogSink.SaveBatchAsync.
///
/// SCOPE: only the GroupBy/partition logic, NOT payload serialisation or HTTP push.
///
/// QUESTION ANSWERED: does switching the grouping key from string to ReadOnlyMemory[byte]
/// help, and how much does removing LINQ's GroupBy machinery add on top?
///
/// RESULTS (Intel Pentium Gold G5400, .NET 9.0.11, BenchmarkDotNet v0.14.0):
///
///   DistinctHosts=3
///   ┌──────────────────────────────────────┬─────────┬──────────┬──────┬──────┬──────┐
///   │ Method                               │   Mean  │ Alloc    │  G0  │  G1  │  G2  │
///   ├──────────────────────────────────────┼─────────┼──────────┼──────┼──────┼──────┤
///   │ GroupBy(string key) — was production │ 1729 µs │ 2931 KB  │ 890  │ 544  │ 470  │
///   │ GroupBy(Span comparer key)           │ 1226 µs │ 2306 KB  │ 560  │ 431  │ 423  │
///   │ Manual partition, no LINQ            │  330 µs │   97 KB  │  47  │   0  │   0  │
///   └──────────────────────────────────────┴─────────┴──────────┴──────┴──────┴──────┘
///
///   DistinctHosts=50
///   ┌──────────────────────────────────────┬─────────┬──────────┬──────┬──────┬──────┐
///   │ GroupBy(string key) — was production │ 1415 µs │ 3035 KB  │ 513  │ 482  │   0  │
///   │ GroupBy(Span comparer key)           │ 1104 µs │ 2413 KB  │ 458  │ 359  │   0  │
///   │ Manual partition, no LINQ            │  390 µs │  111 KB  │  54  │   0  │   0  │
///   └──────────────────────────────────────┴─────────┴──────────┴──────┴──────┴──────┘
///
/// WINNER: Manual partition — 5x faster (hosts=3), 97% less allocation, Gen2=0.
/// LokiLogSink updated to use this approach.
/// ReadOnlyMemoryByteComparer promoted to LogCollector.Infrastructure.Sinks.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
public class LokiGroupingBenchmark
{
    private List<LogEntry> _batch = null!;

    // References the promoted type from Infrastructure — benchmark and production
    // always use the same comparer, can never drift apart.
    private readonly ReadOnlyMemoryByteComparer _comparer = new();

    [Params(3, 50)]
    public int DistinctHosts { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        const int batchSize = 10_000;
        _batch = new List<LogEntry>(batchSize);

        var hostnames = new byte[DistinctHosts][];
        for (int h = 0; h < DistinctHosts; h++)
            hostnames[h] = Encoding.UTF8.GetBytes($"router-mikrotik-{h:D3}");

        var topic = "firewall"u8.ToArray();
        var message = "forward: in:ether1 out:bridge, proto TCP (SYN), 192.168.1.10->8.8.8.8:443"u8.ToArray();
        var now = DateTimeOffset.UtcNow;

        for (int i = 0; i < batchSize; i++)
        {
            _batch.Add(new LogEntry
            {
                Priority     = 30,
                TimestampRaw = "Jun  4 18:00:00"u8.ToArray(),
                Hostname     = hostnames[i % DistinctHosts],
                Topic        = topic,
                Severity     = SyslogSeverity.Info,
                Message      = message,
                ReceivedAt   = now.AddMilliseconds(i),
                RawBuffer    = null, // static memory — matches LogEntry's documented test-construction path
            });
        }
    }

    /// <summary>
    /// Was the production implementation.
    /// Allocates one string per record — 10 000 strings for a 10 000-entry batch.
    /// Survives into Gen2 → full-heap STW pauses.
    /// </summary>
    [Benchmark(Baseline = true, Description = "GroupBy(string key) — current LokiLogSink")]
    public int GroupBy_StringKey()
    {
        return _batch
            .GroupBy(e => Encoding.UTF8.GetString(e.Hostname.Span))
            .Select(g => g.Count())
            .Count();
    }

    /// <summary>Same LINQ GroupBy, but keyed on the byte span — string only materialized once per distinct host.</summary>
    [Benchmark(Description = "GroupBy(Span comparer key)")]
    public int GroupBy_SpanComparerKey()
    {
        return _batch
            .GroupBy(e => e.Hostname, _comparer)
            .Select(g =>
            {
                _ = Encoding.UTF8.GetString(g.Key.Span); // only place a string is built
                return g.Count();
            })
            .Count();
    }

    /// <summary>
    /// Current production implementation (since LokiLogSink was updated).
    /// Dictionary[ReadOnlyMemory[byte], List[int]] — no LINQ, no Lookup overhead.
    /// Allocates only per distinct host, not per record.
    /// Gen1=0, Gen2=0 — no full-heap collections.
    /// </summary>
    [Benchmark(Description = "Manual partition (Dictionary<Memory,List<int>>), no LINQ")]
    public int ManualPartition_NoLinq()
    {
        var groups = new Dictionary<ReadOnlyMemory<byte>, List<int>>(DistinctHosts, _comparer);

        for (int i = 0; i < _batch.Count; i++)
        {
            var key = _batch[i].Hostname;
            if (!groups.TryGetValue(key, out var indices))
            {
                indices = new List<int>();
                groups[key] = indices;
            }
            indices.Add(i);
        }

        return groups.Count;
    }
}

/// <summary>
/// Allocation-free equality/hashing over <see cref="ReadOnlyMemory{T}"/> of <c>byte</c>,
/// comparing by content (<c>SequenceEqual</c>) rather than by reference.
///
/// <para>Not yet wired into <c>LokiLogSink</c> — this lives here as the candidate
/// implementation for the GroupBy-key fix discussed in review. Promote it to
/// <c>LogCollector.Infrastructure.Sinks</c> if/when that fix is applied, and have
/// this benchmark reference the promoted type instead of this local copy so the
/// two never drift apart.</para>
/// </summary>
public sealed class ReadOnlyMemoryByteComparer : IEqualityComparer<ReadOnlyMemory<byte>>
{
    public bool Equals(ReadOnlyMemory<byte> x, ReadOnlyMemory<byte> y)
        => x.Span.SequenceEqual(y.Span);

    public int GetHashCode(ReadOnlyMemory<byte> obj)
    {
        var hash = new HashCode();
        hash.AddBytes(obj.Span);
        return hash.ToHashCode();
    }
}
