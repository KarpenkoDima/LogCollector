namespace LogCollector.Infrastructure.Persistence;

/// <summary>
/// SQLite connection settings.
/// Bind to the <c>"Sqlite"</c> section in <c>appsettings.json</c>:
/// <code>
/// "Sqlite": {
///   "ConnectionString": "Data Source=/var/log/logcollector/logs.db"
/// }
/// </code>
/// Override via environment variable (double-underscore is the section separator):
/// <code>
/// SQLITE__CONNECTIONSTRING="Data Source=/mnt/fast-ssd/logs.db"
/// </code>
/// </summary>
/// <remarks>
/// Keeping the connection string here — rather than in <c>BatchWriterOptions</c> —
/// maintains a clean boundary: batch-writing timing concerns (<c>BatchSize</c>,
/// <c>BatchTimeout</c>) are separate from storage location concerns.
/// If a second repository implementation is added (e.g. Postgres), it gets its own
/// options class and the batch-writer options remain unchanged.
/// </remarks>
public sealed class SqliteOptions
{
    public string ConnectionString { get; set; } = "Data Source=logs.db";
}
