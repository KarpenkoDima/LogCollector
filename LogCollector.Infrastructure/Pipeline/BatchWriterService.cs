using System.Threading.Channels;
using LogCollector.Application.Pipeline;
using LogCollector.Core.Domain;
using Microsoft.Extensions.Hosting;

namespace LogCollector.Infrastructure.Pipeline;

public sealed class BatchWriterService : BackgroundService
{
    private readonly ChannelReader<LogEntry> _reader;
    private readonly BatchProcessor _processor;

    public BatchWriterService(ChannelReader<LogEntry> reader, BatchProcessor processor)
    {
        _reader = reader;
        _processor = processor;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        _processor.RunAsync(_reader, stoppingToken);
}
