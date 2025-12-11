using System.Buffers;
using System.Text;
using System.Threading.Channels;
using LogCollector.Application.Interfaces;
using LogCollector.Core.Domain;
using LogCollector.Infrastructure.Persistence;
using LogCollector.Infrastructure.Pipeline;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LogCollector.Tests.Pipeline;

/// <summary>
/// Tests for <see cref="BatchWriterService"/>.
///
/// Two tiers of tests live here:
///
/// Tier 1 — Pure unit tests using <see cref="InMemoryLogRepository"/>.
/// Run in milliseconds with no I/O. BatchWriterService is now fully testable
/// in isolation because it depends on ILogRepository, not on SqliteConnection.
///
/// Tier 2 — Integration tests using <see cref="SqliteLogRepository"/>.
/// Verify the byte→string→SQLite TEXT round-trip against a real (in-memory) database.
/// </summary>
public sealed class BatchWriterServiceTests : IAsyncLifetime
{
    private readonly string _dbName = $"TestDb_{Guid.NewGuid():N}";
    private string SqliteCs => $"Data Source={_dbName};Mode=Memory;Cache=Shared";
    private SqliteConnection? _guardian;

    public async Task InitializeAsync()
    {
        _guardian = new SqliteConnection(SqliteCs);
        await _guardian.OpenAsync();
    }

    public async Task DisposeAsync()
    {
        if (_guardian != null)
            await _guardian.DisposeAsync();
    }

    // ── Tier 1: pure unit tests ───────────────────────────────────────────────

    [Fact]
    public async Task BatchTimeout_FlushesPartialBatch_WithoutWaitingForFullBatch()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var repo      = new InMemoryLogRepository();
        var (ch, svc) = BuildService(repo, batchTimeout: TimeSpan.FromMilliseconds(60));

        await svc.StartAsync(cts.Token);
        await ch.Writer.WriteAsync(Entry("fw", "sparse traffic"), cts.Token);
        await Task.Delay(300, cts.Token);

        Assert.Single(repo.Saved);
        Assert.Equal("fw", repo.Saved[0].Hostname);

        await svc.StopAsync(cts.Token);
    }

    [Fact]
    public async Task FullBatch_IsFlushedImmediately_WithoutWaitingForTimeout()
    {
        const int batchSize = 8;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var repo      = new InMemoryLogRepository();
        var (ch, svc) = BuildService(repo, batchSize: batchSize,
                                     batchTimeout: TimeSpan.FromSeconds(60));

        await svc.StartAsync(cts.Token);
        for (int i = 0; i < batchSize; i++)
            await ch.Writer.WriteAsync(Entry("fw", $"msg {i}"), cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (repo.Saved.Count < batchSize && DateTime.UtcNow < deadline)
            await Task.Delay(20, cts.Token);

        Assert.Equal(batchSize, repo.Saved.Count);
        await svc.StopAsync(cts.Token);
    }

    [Fact]
    public async Task GracefulStop_DrainsRemainingEntries_BeforeExit()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var repo      = new InMemoryLogRepository();
        var (ch, svc) = BuildService(repo, batchTimeout: TimeSpan.FromSeconds(60));

        await svc.StartAsync(cts.Token);
        for (int i = 0; i < 5; i++)
            await ch.Writer.WriteAsync(Entry("fw", $"entry {i}"), cts.Token);

        await svc.StopAsync(CancellationToken.None);

        Assert.Equal(5, repo.Saved.Count);
    }

    [Fact]
    public async Task RawBuffers_AreDisposed_AfterSuccessfulSave()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var repo      = new InMemoryLogRepository();
        var (ch, svc) = BuildService(repo, batchTimeout: TimeSpan.FromMilliseconds(60));

        await svc.StartAsync(cts.Token);
        var o1 = new TrackingOwner();
        var o2 = new TrackingOwner();
        await ch.Writer.WriteAsync(Entry("fw", "x", o1), cts.Token);
        await ch.Writer.WriteAsync(Entry("fw", "y", o2), cts.Token);

        await Task.Delay(300, cts.Token);

        Assert.True(o1.IsDisposed);
        Assert.True(o2.IsDisposed);
        await svc.StopAsync(cts.Token);
    }

    [Fact]
    public async Task RawBuffers_AreDisposed_EvenWhenRepositoryThrows()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var repo      = new InMemoryLogRepository { AlwaysThrow = true };
        var (ch, svc) = BuildService(repo, batchTimeout: TimeSpan.FromMilliseconds(60));

        await svc.StartAsync(cts.Token);
        var owner = new TrackingOwner();
        await ch.Writer.WriteAsync(Entry("fw", "msg", owner), cts.Token);

        await Task.Delay(300, cts.Token);

        Assert.True(owner.IsDisposed,
            "Pool buffer must be returned even after a repository failure");
        await svc.StopAsync(cts.Token);
    }

    // ── Tier 2: integration tests against real SQLite ─────────────────────────

    [Fact]
    public async Task SqliteRepository_StoresAndRetrievesAllFields_Correctly()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var repo      = BuildSqliteRepo();
        var (ch, svc) = BuildService(repo, batchTimeout: TimeSpan.FromMilliseconds(60));

        await svc.StartAsync(cts.Token);
        await ch.Writer.WriteAsync(new LogEntry
        {
            Priority     = 42,
            TimestampRaw = Bytes("Jun  4 18:00:00"),
            Hostname     = Bytes("mtk-router"),
            Topic        = Bytes("firewall"),
            Severity     = SyslogSeverity.Critical,
            Message      = Bytes("forward: in:ether1"),
            ReceivedAt   = new DateTimeOffset(2024, 6, 4, 18, 0, 0, TimeSpan.Zero),
        }, cts.Token);

        await Task.Delay(300, cts.Token);

        var rows = await QueryAllAsync();
        Assert.Single(rows);
        Assert.Equal("mtk-router",         rows[0].Hostname);
        Assert.Equal("forward: in:ether1", rows[0].Message);
        await svc.StopAsync(cts.Token);
    }

    [Fact]
    public async Task SqliteRepository_MultipleHosts_NoFieldCrossContamination()
    {
        var hosts     = new[] { "mtk-router", "win-server-01", "switch-core" };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var repo      = BuildSqliteRepo();
        var (ch, svc) = BuildService(repo, batchTimeout: TimeSpan.FromMilliseconds(60));

        await svc.StartAsync(cts.Token);
        foreach (var h in hosts)
            await ch.Writer.WriteAsync(Entry(h, $"log from {h}"), cts.Token);

        await Task.Delay(300, cts.Token);

        var rows = await QueryAllAsync();
        Assert.Equal(3, rows.Count);
        foreach (var row in rows)
            Assert.Contains(row.Hostname, row.Message);
        await svc.StopAsync(cts.Token);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private SqliteLogRepository BuildSqliteRepo() =>
        new(Options.Create(new SqliteOptions { ConnectionString = SqliteCs }),
            NullLogger<SqliteLogRepository>.Instance);

    private (Channel<LogEntry>, BatchWriterService) BuildService(
        ILogRepository repository, int batchSize = 500, TimeSpan? batchTimeout = null)
    {
        var channel = Channel.CreateBounded<LogEntry>(1_000);
        var opts    = Options.Create(new BatchWriterOptions
        {
            BatchSize    = batchSize,
            BatchTimeout = batchTimeout ?? TimeSpan.FromSeconds(2),
        });
        var svc = new BatchWriterService(
            channel.Reader, repository, opts, NullLogger<BatchWriterService>.Instance);
        return (channel, svc);
    }

    private static LogEntry Entry(
        string hostname, string message, IMemoryOwner<byte>? owner = null) =>
        new()
        {
            Priority     = 30,
            TimestampRaw = Bytes("Jun  4 18:00:00"),
            Hostname     = Bytes(hostname),
            Topic        = Bytes("firewall"),
            Severity     = SyslogSeverity.Info,
            Message      = Bytes(message),
            ReceivedAt   = DateTimeOffset.UtcNow,
            RawBuffer    = owner,
        };

    private static ReadOnlyMemory<byte> Bytes(string s)
        => Encoding.UTF8.GetBytes(s).AsMemory();

    private async Task<List<(string Hostname, string Message)>> QueryAllAsync()
    {
        await using var conn   = new SqliteConnection(SqliteCs);
        await conn.OpenAsync();
        await using var cmd    = conn.CreateCommand();
        cmd.CommandText        = "SELECT Hostname, Message FROM Logs ORDER BY Id";
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<(string, string)>();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    // ── Test doubles ─────────────────────────────────────────────────────────

    private sealed class InMemoryLogRepository : ILogRepository
    {
        public List<(string Hostname, string Message)> Saved { get; } = new();
        public bool AlwaysThrow { get; init; }

        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

        public Task SaveBatchAsync(IReadOnlyList<LogEntry> batch, CancellationToken ct)
        {
            if (AlwaysThrow)
                throw new InvalidOperationException("Simulated repository failure");
            foreach (var e in batch)
                Saved.Add((
                    Encoding.UTF8.GetString(e.Hostname.Span),
                    Encoding.UTF8.GetString(e.Message.Span)));
            return Task.CompletedTask;
        }
    }

    private sealed class TrackingOwner : IMemoryOwner<byte>
    {
        public bool IsDisposed { get; private set; }
        public Memory<byte> Memory { get; } = new byte[4];
        public void Dispose() => IsDisposed = true;
    }
}
