using System.Data;
using Dapper;
using LogCollector.Core.Interfaces;
using LogCollector.Core.Models;
using Microsoft.Data.Sqlite;

namespace LogCollector.Infrastructure.Persistence;

/// <summary>
/// Реализация репозитория для SQLite через Dapper.
/// Заменить на PostgreSQL/ClickHouse — поменять строку подключения и диалект SQL.
/// Интерфейс ILogRepository не меняется.
/// </summary>
public sealed class SqliteLogRepository : ILogRepository
{
    private readonly string _connectionString;

    public SqliteLogRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>
    /// Инициализация схемы БД при старте сервиса.
    /// Вызывается один раз из Program.cs.
    /// </summary>
    public async Task InitializeAsync()
    {
        Console.WriteLine($"DB path: {Path.GetFullPath(_connectionString.Replace("Data Source=", ""))}");
        // Извлекаем путь к файлу из строки подключения и создаём директорию если нет
        var builder = new SqliteConnectionStringBuilder(_connectionString);
        var dbPath = builder.DataSource;

        if (false == string.IsNullOrEmpty(dbPath) && dbPath != ":memory:")
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
            if (false == string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir); // не падает если директория уже есть
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        await connection.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS logs (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                source    TEXT    NOT NULL,
                timestamp TEXT    NOT NULL,
                level     TEXT    NOT NULL,
                message   TEXT    NOT NULL,
                source_ip TEXT,
                hostname  TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_logs_timestamp ON logs(timestamp);
            CREATE INDEX IF NOT EXISTS idx_logs_source    ON logs(source);
            """);
    }

    public async Task<int> InsertBatchAsync(
        IReadOnlyList<LogEntry> entries,
        CancellationToken ct = default)
    {
        // Соединение на каждый батч — SQLite не thread-safe при shared connection.
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        // Один BatchCommit на весь батч = один fsync.
        // Без транзакции SQLite делает fsync на каждую INSERT:
        // 500 записей = 500 fsync. С транзакцией = 1 fsync.
        await using var transaction = await connection.BeginTransactionAsync(ct);

        try
        {
            var affected = await connection.ExecuteAsync(
                new CommandDefinition(
                    commandText: """
                        INSERT INTO logs (source, timestamp, level, message, source_ip, hostname)
                        VALUES (@Source, @Timestamp, @Level, @Message, @SourceIp, @Hostname)
                        ON CONFLICT DO NOTHING
                        """,
                    parameters:        entries,   // Dapper сам итерирует IReadOnlyList<T>
                    transaction:       (IDbTransaction)transaction,
                    cancellationToken: ct));       // CommandDefinition передаёт токен в драйвер

            await transaction.CommitAsync(ct);
            return affected;
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }
}
