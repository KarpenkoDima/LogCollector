namespace LogCollector.Infrastructure.Pipeline;

/// <summary>
/// Batch-writing timing configuration for <see cref="BatchWriterService"/>.
/// Bind to the <c>"BatchWriter"</c> section in <c>appsettings.json</c>.
///
/// The SQLite connection string has moved to <c>SqliteOptions</c> so that
/// timing concerns and storage-location concerns remain in separate classes.
/// </summary>
public sealed class BatchWriterOptions
{
    /// <summary>
    /// Maximum entries per SQLite transaction.
    /// 500 rows per COMMIT gives ~20× throughput improvement over row-by-row inserts
    /// on a typical SSD (one fsync instead of 500).
    /// </summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>
    /// Maximum wait before flushing a partial batch.
    /// Prevents entries from stalling in the channel during low-traffic periods.
    /// Format: <c>"HH:MM:SS"</c> — e.g. <c>"00:00:02"</c> for two seconds.
    /// </summary>
    public TimeSpan BatchTimeout { get; set; } = TimeSpan.FromSeconds(2);
}
