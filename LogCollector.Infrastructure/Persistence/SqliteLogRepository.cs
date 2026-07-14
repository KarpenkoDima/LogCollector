using System.Data;
using System.Text;
using Dapper;
using LogCollector.Application.Interfaces;
using LogCollector.Core.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace LogCollector.Infrastructure.Persistence;

/// <summary>
/// Single-writer SQLite repository. WAL permits concurrent readers, while one persistent
/// connection and one transaction per batch avoid connection and fsync overhead per row.
/// UTF-8 slices become strings only at this persistence boundary.
/// </summary>
public sealed class SqliteLogRepository : ILogRepository
{
    private const string InsertSql =
        """
        INSERT INTO logs
            (priority, facility, severity, device_timestamp, hostname, topic, message, received_at_unix_ms)
        VALUES
            (@Priority, @Facility, @Severity, @DeviceTimestamp, @Hostname, @Topic, @Message, @ReceivedAtUnixMs);
        """;

    private readonly SqliteOptions _options;
    private SqliteConnection? _connection;

    public SqliteLogRepository(IOptions<SqliteOptions> options) => _options = options.Value;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
            return;

        var connection = new SqliteConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            string schema = $$"""
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA temp_store = MEMORY;
                PRAGMA busy_timeout = {{_options.BusyTimeoutSeconds * 1_000}};

                CREATE TABLE IF NOT EXISTS logs (
                    id                    INTEGER PRIMARY KEY AUTOINCREMENT,
                    priority              INTEGER NOT NULL,
                    facility              INTEGER NOT NULL,
                    severity              INTEGER NOT NULL,
                    device_timestamp      TEXT NOT NULL,
                    hostname              TEXT NOT NULL,
                    topic                 TEXT NULL,
                    message               TEXT NOT NULL,
                    received_at_unix_ms   INTEGER NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_logs_received_at
                    ON logs(received_at_unix_ms);
                CREATE INDEX IF NOT EXISTS ix_logs_hostname_received_at
                    ON logs(hostname, received_at_unix_ms);
                """;

            await connection.ExecuteAsync(new CommandDefinition(
                schema,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            _connection = connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task SaveBatchAsync(
        IReadOnlyList<LogEntry> entries,
        CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
            return;

        SqliteConnection connection = _connection ??
            throw new InvalidOperationException("Repository is not initialized.");

        var rows = new LogRow[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            LogEntry entry = entries[i];
            rows[i] = new LogRow
            {
                Priority = entry.Priority,
                Facility = entry.Facility,
                Severity = (int)entry.Severity,
                DeviceTimestamp = Decode(entry.Timestamp),
                Hostname = Decode(entry.Hostname),
                Topic = entry.Topic.IsEmpty ? null : Decode(entry.Topic),
                Message = Decode(entry.Message),
                ReceivedAtUnixMs = entry.ReceivedAt.ToUnixTimeMilliseconds(),
            };
        }

        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            InsertSql,
            rows,
            transaction,
            commandType: CommandType.Text,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private static string Decode(ReadOnlyMemory<byte> value) => Encoding.UTF8.GetString(value.Span);

    private sealed class LogRow
    {
        public required int Priority { get; init; }
        public required int Facility { get; init; }
        public required int Severity { get; init; }
        public required string DeviceTimestamp { get; init; }
        public required string Hostname { get; init; }
        public string? Topic { get; init; }
        public required string Message { get; init; }
        public required long ReceivedAtUnixMs { get; init; }
    }
}
