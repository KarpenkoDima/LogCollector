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
public sealed class SqliteLogSink : ILogSink, IDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteLogSink> _logger;

    // Живут всё время работы синка
    private SqliteConnection? _conn;
    private SqliteCommand? _insertCommand;

    private SqliteParameter? _pPri, _pTs, _pHost, _pTopic, _pSev, _pMsg, _pRecv;

    public SqliteLogSink(string connectionString, ILogger<SqliteLogSink> logger)
    {
        _connectionString = connectionString;
        _logger           = logger;
    }

    public void Dispose()
    {
        _insertCommand?.Dispose();
        _conn?.Dispose();
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        _conn = new SqliteConnection(_connectionString);
        await _conn.OpenAsync(ct);

        await using var pragma = _conn.CreateCommand();
        pragma.CommandText = 
            "PRAGMA journal_mode=WAL; " +
            "PRAGMA synchronous=NORMAL; " +
            "PRAGMA busy_timeout=5000;";
        await pragma.ExecuteNonQueryAsync(ct);

        await using var ddl = _conn.CreateCommand();
        ddl.CommandText =
            """
            CREATE TABLE IF NOT EXISTS Logs (
                Id           INTEGER PRIMARY KEY AUTOINCREMENT,
                Priority     INTEGER NOT NULL,
                TimestampRaw TEXT    NOT NULL,
                Hostname     TEXT    NOT NULL,
                Topic        TEXT    NOT NULL,
                Severity     INTEGER NOT NULL,
                Message      TEXT    NOT NULL,
                ReceivedAt   INTEGER NOT NULL -- Unix milliseconds (UTC)
            );
            CREATE INDEX IF NOT EXISTS idx_logs_received_at ON Logs(ReceivedAt);
            CREATE INDEX IF NOT EXISTS idx_logs_hostname    ON Logs(Hostname);
            CREATE INDEX IF NOT EXISTS idx_logs_severity    ON Logs(Severity);
            """;
        await ddl.ExecuteNonQueryAsync(ct);

        _insertCommand = _conn.CreateCommand();
        _insertCommand.CommandText =
            """
            INSERT INTO Logs (Priority, TimestampRaw, Hostname, Topic, Severity, Message, ReceivedAt)
            VALUES           (@pri,      @ts,           @host,  @topic, @sev,    @msg,      @recv)
            """;

        _pPri = _insertCommand.Parameters.Add("@pri", SqliteType.Integer);
        _pTs = _insertCommand.Parameters.Add("@ts", SqliteType.Text);
        _pHost = _insertCommand.Parameters.Add("@host", SqliteType.Text);
        _pTopic = _insertCommand.Parameters.Add("@topic", SqliteType.Text);
        _pSev = _insertCommand.Parameters.Add("@sev", SqliteType.Integer);
        _pMsg = _insertCommand.Parameters.Add("@msg", SqliteType.Text);
        _pRecv = _insertCommand.Parameters.Add("@recv", SqliteType.Integer);

        _insertCommand.Prepare(); //

        _logger.LogInformation("[Sqlite] Schema ready — {Conn}", _connectionString);
    }

    public async Task SaveBatchAsync(IReadOnlyList<LogEntry> batch, CancellationToken ct)
    {
        await using var tx = await _conn!.BeginTransactionAsync(ct);
        _insertCommand!.Transaction = (SqliteTransaction)tx;

        foreach (var entry in batch)
        {

            _pPri!.Value = entry.Priority;
            _pTs!.Value = Encoding.UTF8.GetString(entry.TimestampRaw.Span);
            _pHost!.Value = Encoding.UTF8.GetString(entry.Hostname.Span);
            _pTopic!.Value = Encoding.UTF8.GetString(entry.Topic.Span);
            _pSev!.Value = (int)entry.Severity;
            _pMsg!.Value = Encoding.UTF8.GetString(entry.Message.Span);
            _pRecv!.Value = entry.ReceivedAt.ToUnixTimeMilliseconds();

            await _insertCommand.ExecuteNonQueryAsync(ct);
        }        

        await tx.CommitAsync(ct);
    }
}
