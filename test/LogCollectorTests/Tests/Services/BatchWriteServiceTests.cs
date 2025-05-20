using Castle.Core.Logging;
using LogCollector.Application.Channels;
using LogCollector.Application.Options;
using LogCollector.Application.Services;
using LogCollector.Core.Interfaces;
using LogCollector.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace LogCollectorTests.Tests.Services;

public class BatchWriteServiceTests
{
    private readonly LogChannel _channel;
    private readonly Mock<ILogRepository> _repositoryMock;
    private readonly LogCollectorOptions _testOptions;

    public BatchWriteServiceTests()
    {
        _channel = new LogChannel();
        _repositoryMock = new Mock<ILogRepository>();

        _testOptions = new LogCollectorOptions
        {
            BatchSize=3,
            FlushInterval=1,
        };
    }

    private BatchWriteService CreateService(int timeoutInSeconds = 1)
    {
        _testOptions.FlushInterval = timeoutInSeconds;
        var optionsMock = Options.Create(_testOptions);

        return new BatchWriteService(
            _channel,
            _repositoryMock.Object,
            NullLogger<BatchWriteService>.Instance,
            optionsMock);
    }

    [Fact]
    public async Task ExecuteAsync_WhenBatchSizeReached_ShouldFlushImmediately()
    {
        // Arrange
        var service = CreateService(timeoutInSeconds: 10); // a long timeout: ensure the size limit is met
        using var cts = new CancellationTokenSource();

        // Starting service in background
        var executeTask = service.StartAsync(cts.Token);

        // ACT
        // Send exactly 3 logs (out BatchSize) per channel
        for (int i = 0; i < 3; i++)
        {
            await _channel.Writer.WriteAsync(new LogEntry
            {
                Source = "Test",
                Timestamp = DateTime.UtcNow,
                Level = "Info",
                Message = $"Log-{i}"
            });
        }

        await Task.Delay(150);

        // Assert 
        // check repository was called once with the list of 3 exactly elements
        _repositoryMock.Verify(r => r.InsertBatchAsync(
            It.Is<IReadOnlyList<LogEntry>>(list => list.Count == 3),
            It.IsAny<CancellationToken>()),
            Times.Once);

        // Cleanup
        cts.Cancel();

        // We wrap this to catch the expected cancellation exception when the service stops
        try
        {
            await executeTask;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
