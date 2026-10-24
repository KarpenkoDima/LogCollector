namespace LogCollector.Application.Interfaces;

/// <summary>
/// Indicates that a persistence operation failed for a reason that is expected to be
/// temporary and may succeed if the same batch is retried.
/// </summary>
public sealed class TransientLogPersistenceException : Exception
{
    public TransientLogPersistenceException()
    {
    }

    public TransientLogPersistenceException(string message)
        : base(message)
    {
    }

    public TransientLogPersistenceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
