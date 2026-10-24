namespace LogCollector.Application.Pipeline;

public sealed class BatchProcessorOptions
{
    public const string SectionName = "Pipeline";

    public int Capacity { get; set; } = 2_048;
    public int BatchSize { get; set; } = 500;
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Total number of attempts for a transient persistence failure, including the first attempt.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Maximum time allowed to persist buffered records after cancellation.</summary>
    public TimeSpan ShutdownFlushTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
