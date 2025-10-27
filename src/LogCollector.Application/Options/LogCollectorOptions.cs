namespace LogCollector.Application.Options;

public class LogCollectorOptions
{
    public int BatchSize { get; set; } = 500;
    public int ChannelCapacity { get; set; } = 10_000;
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Дедлайн финального flush при shutdown. Держать меньше HostOptions.ShutdownTimeout.</summary>
    public TimeSpan ShutdownFlushTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
