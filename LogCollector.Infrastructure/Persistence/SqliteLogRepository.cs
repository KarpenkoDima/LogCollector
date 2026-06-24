using System.Globalization;
using System.Text;
using LogCollector.Application.Interfaces;
using LogCollector.Core.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LogCollector.Infrastructure.Persistence;

/// <summary>
/// SQLite implementation of <see cref="ILogRepository"/>.
///
/// <para>
/// All knowledge of SQLite — the connection string, the schema DDL, parameter
/// binding, WAL mode — is contained here.  <c>BatchWriterService</c> only ever
/// calls <see cref="ILogRepository"/> and never touches a <see cref="SqliteConnection"/>
/// directly.
/// </para>
///
/// <para><b>String allocation rule:</b></para>
/// <para>
/// <c>Encoding.UTF8.GetString(span)</c> is called inside <see cref="SaveBatchAsync"/>,
/// which is the only place in the entire pipeline that converts bytes to strings.
/// Everything upstream operated on <c>ReadOnlyMemory&lt;byte&gt;</c>.
/// </para>
/// </summary>
public sealed class SqliteLogRepository : ILogRepository
{
    private readonly SqliteOptions _options;
    private readonly ILogger<SqliteLogRepository> _logger;

    public SqliteLogRepository(
        IOptions<SqliteOptions> options,
        ILogger<SqliteLogRepository> logger)
    {
        _options = options.Value;
        _logger  = logger;
    }

    // ── ILogRepository ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_options.ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();

        // WAL mode: writer and readers run concurrently without blocking each other.
        // synchronous=NORMAL is safe with WAL and far faster than the default FULL.
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

        _logger.LogInformation("SQLite schema ready — {Conn}", _options.ConnectionString);
    }

    /// <inheritdoc/>
    public async Task SaveBatchAsync(IReadOnlyList<LogEntry> batch, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_options.ConnectionString);
        await conn.OpenAsync(ct);

        await using var tx  = await conn.BeginTransactionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;

        cmd.CommandText =
            """
            INSERT INTO Logs (Priority, TimestampRaw, Hostname, Topic, Severity, Message, ReceivedAt)
            VALUES           (@pri,     @ts,          @host,    @topic, @sev,    @msg,    @recv)
            """;

        // Add parameters once; reassign .Value in the loop — no per-row re-allocation.
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
            // ── String allocation point ───────────────────────────────────────
            // Every GetString call allocates one string — exactly here, at the
            // database boundary, as the knowledge-base.md rule requires.
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
