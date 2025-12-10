using System.Globalization;
using System.Text;
using LogCollector.Application.Interfaces;
using LogCollector.Core.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LogCollector.Infrastructure.Sinks;

/// <summary>
/// Persists log entries to a local SQLite database.
/// Implements <see cref="ILogSink"/> so it participates in the fanout pipeline.
/// </summary>
public sealed class SqliteLogSink : ILogSink
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteLogSink> _logger;

    public SqliteLogSink(string connectionString, ILogger<SqliteLogSink> logger)
    {
        _connectionString = connectionString;
        _logger           = logger;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        await cmd.ExecuteNonQueryAsync(ct);

        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS Logs (
                Id           INTEGER PRIMARY KEY AUTOINCREMENT,
                Priority     INTEGER NOT NULL,
                TimestampRaw TEXT    NOT NULL,
                Hostname     TEXT    NOT NULL,
                Topic        TEXT    NOT NULL,
                Severity     INTEGER NOT NULL,
                Message      TEXT    NOT NULL,
                ReceivedAt   TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_logs_received_at ON Logs(ReceivedAt);
            CREATE INDEX IF NOT EXISTS idx_logs_hostname    ON Logs(Hostname);
            CREATE INDEX IF NOT EXISTS idx_logs_severity    ON Logs(Severity);
            """;
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("[Sqlite] Schema ready — {Conn}", _connectionString);
    }

    public async Task SaveBatchAsync(IReadOnlyList<LogEntry> batch, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var tx  = await conn.BeginTransactionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText =
            """
            INSERT INTO Logs (Priority, TimestampRaw, Hostname, Topic, Severity, Message, ReceivedAt)
            VALUES           (@pri,     @ts,          @host,    @topic, @sev,    @msg,    @recv)
            """;

        var pPri   = cmd.Parameters.Add("@pri",   SqliteType.Integer);
        var pTs    = cmd.Parameters.Add("@ts",    SqliteType.Text);
        var pHost  = cmd.Parameters.Add("@host",  SqliteType.Text);
        var pTopic = cmd.Parameters.Add("@topic", SqliteType.Text);
        var pSev   = cmd.Parameters.Add("@sev",   SqliteType.Integer);
        var pMsg   = cmd.Parameters.Add("@msg",   SqliteType.Text);
        var pRecv  = cmd.Parameters.Add("@recv",  SqliteType.Text);

        cmd.Prepare();

        foreach (var entry in batch)
        {
            pPri.Value   = entry.Priority;
            pTs.Value    = Encoding.UTF8.GetString(entry.TimestampRaw.Span);
            pHost.Value  = Encoding.UTF8.GetString(entry.Hostname.Span);
            pTopic.Value = Encoding.UTF8.GetString(entry.Topic.Span);
            pSev.Value   = (int)entry.Severity;
            pMsg.Value   = Encoding.UTF8.GetString(entry.Message.Span);
            pRecv.Value  = entry.ReceivedAt.ToString("o", CultureInfo.InvariantCulture);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }
}
