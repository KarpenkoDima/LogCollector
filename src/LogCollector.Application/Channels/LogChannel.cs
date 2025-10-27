using LogCollector.Application.Options;
using LogCollector.Core.Models;
using Microsoft.Extensions.Options;
using System.Threading.Channels;

namespace LogCollector.Application.Channels;

public sealed class LogChannel
{
    private readonly Channel<LogEntry> _channel;

    public LogChannel(IOptions<LogCollectorOptions> options)
    {
        _channel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(options.Value.ChannelCapacity)
        {
            // SingleReader = true — читает только BatchWriterService.
            // Channel выбирает облегчённую реализацию без лишней синхронизации на чтении.
            SingleReader = true,
            SingleWriter = false, // пишут несколько сетевых слушателей
            FullMode = BoundedChannelFullMode.Wait
        });
    }
    public ChannelWriter<LogEntry> Writer => _channel.Writer;
    public ChannelReader<LogEntry> Reader => _channel.Reader;
}
