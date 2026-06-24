using LogCollector.Core.Domain;

namespace LogCollector.Application.Interfaces;

/// <summary>
/// Defines the persistence contract for syslog entries.
///
/// <para>
/// This interface lives in the Application layer so that the pipeline
/// (<see cref="LogCollector.Infrastructure.Pipeline.BatchWriterService"/>) depends on
/// an abstraction, not on a concrete database technology.  The SQLite implementation
/// (<c>SqliteLogRepository</c>) lives in Infrastructure and wires up via DI.
/// </para>
///
/// <para>
/// Benefit for testing: unit tests for <c>BatchWriterService</c> can inject an
/// in-memory fake without involving a real database at all.
/// </para>
/// </summary>
public interface ILogRepository
{
    /// <summary>
    /// Ensures the underlying storage is ready to accept entries.
    /// For a relational store this means creating the table and indexes if they
    /// do not already exist.  Called once at service startup.
    /// </summary>
    Task InitializeAsync(CancellationToken ct);

    /// <summary>
    /// Persists a batch of log entries atomically.  All entries in
    /// <paramref name="batch"/> must be committed together or not at all.
    /// </summary>
    /// <param name="batch">A non-empty, ordered list of entries to persist.</param>
    /// <param name="ct">Propagates the host shutdown signal.</param>
    Task SaveBatchAsync(IReadOnlyList<LogEntry> batch, CancellationToken ct);
}
