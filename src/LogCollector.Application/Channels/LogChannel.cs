using LogCollector.Core.Models;
using System.Threading.Channels;

namespace LogCollector.Application.Channels;

public sealed class LogChannel
{
    private readonly Channel<LogEntry> _channel = Channel.CreateBounded<LogEntry>(
        new BoundedChannelOptions(capacity: 10_000)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

    public ChannelWriter<LogEntry> Writer => _channel.Writer;
    public ChannelReader<LogEntry> Reader => _channel.Reader;
}
