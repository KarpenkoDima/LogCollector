using Dapper;
using LogCollector.Core.Domain;
using LogCollector.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Xunit;

namespace LogCollector.Tests.Persistence;

public sealed class SqliteLogRepositoryTests
{
    [Fact]
    public async Task InitializesSchemaAndPersistsBatch()
    {
        string path = Path.Combine(Path.GetTempPath(), $"logcollector-{Guid.NewGuid():N}.db");
        string connectionString = $"Data Source={path}";

        try
        {
            await using var repository = new SqliteLogRepository(
                Options.Create(new SqliteOptions { ConnectionString = connectionString }));
            await repository.InitializeAsync(CancellationToken.None);

            LogEntry entry = new()
            {
                Priority = 132,
                Timestamp = "Jul 14 12:30:45"u8.ToArray(),
                Hostname = "edge-router"u8.ToArray(),
                Topic = "firewall"u8.ToArray(),
                Message = "forward: accepted"u8.ToArray(),
                ReceivedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_752_496_245_000),
            };
            await repository.SaveBatchAsync([entry], CancellationToken.None);

            await using var connection = new SqliteConnection(connectionString);
            StoredLog stored = await connection.QuerySingleAsync<StoredLog>(
                """
                SELECT priority, facility, severity, hostname, topic, message,
                       received_at_unix_ms AS ReceivedAtUnixMs
                FROM logs;
                """);

            Assert.Equal(132, stored.Priority);
            Assert.Equal(16, stored.Facility);
            Assert.Equal((int)SyslogSeverity.Warning, stored.Severity);
            Assert.Equal("edge-router", stored.Hostname);
            Assert.Equal("firewall", stored.Topic);
            Assert.Equal("forward: accepted", stored.Message);
            Assert.Equal(1_752_496_245_000, stored.ReceivedAtUnixMs);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
            File.Delete(path + "-shm");
            File.Delete(path + "-wal");
        }
    }

    private sealed class StoredLog
    {
        public int Priority { get; init; }
        public int Facility { get; init; }
        public int Severity { get; init; }
        public string Hostname { get; init; } = string.Empty;
        public string? Topic { get; init; }
        public string Message { get; init; } = string.Empty;
        public long ReceivedAtUnixMs { get; init; }
    }
}
