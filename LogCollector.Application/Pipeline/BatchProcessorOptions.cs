namespace LogCollector.Application.Pipeline;

public sealed class BatchProcessorOptions
{
    public const string SectionName = "Pipeline";

    public int Capacity { get; set; } = 10_000;
    public int BatchSize { get; set; } = 500;
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);
}
