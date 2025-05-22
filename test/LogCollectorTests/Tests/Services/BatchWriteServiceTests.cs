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

    private static async Task WaitUntilAsync(
        Func<bool> continuation,
        string timeoutMessage,
        TimeSpan? timeout=null
        )
    {
        var deadline = DateTime.UtcNow + (timeout ??  TimeSpan.FromSeconds(50));

        while (DateTime.UtcNow < deadline)
        {
            if (continuation())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException(timeoutMessage);

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
        var entries = Enumerable.Range(0, 3)
            .Select(i => new LogEntry 
            { 
                Message = $"log {i} ",
                Timestamp=DateTime.UtcNow,
                Source = "MikroTik",
                Level = "low"
            }).ToList();


        // 1. Create TaskCompletionSource for synchronization threads
        var tcs = new TaskCompletionSource();

        // Variable for save actual numbers of elements in moment calls.
        int actualBatcCount = 0;

        _repositoryMock.Setup(r => r.InsertBatchAsync(It.IsAny<IReadOnlyList<LogEntry>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3)
            .Callback<IReadOnlyList<LogEntry>, CancellationToken>((list, token)=>
            {
                // 2. Save Count BEFORE, the worker calls batch.Clear()!
                actualBatcCount = list.Count;

                // 3. We signal method the main test thread that methdod has been called
                tcs.TrySetResult();
            });

        var service = CreateService(timeoutInSeconds: 10);
        using var cts = new CancellationTokenSource();

        // Act
        await service.StartAsync(cts.Token);

        foreach (var item in entries)
        {
            await _channel.Writer.WriteAsync(item);
        }

        // Waiting for signal from mock (with timeout 5 seconds for security)
        // This replace out WaitUntilAsync, but works without loops and instantaneous.
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        // Assert
        // Check that mock was called once
        _repositoryMock.Verify(r => r.InsertBatchAsync(
            It.IsAny<IReadOnlyList<LogEntry>>(),
            It.IsAny<CancellationToken>()),
            Times.Once);

        Assert.Equal(3, actualBatcCount);
    }
    [Fact]
    public async Task ExecuteAsync_WhenRepositoryThrpws_ShouldNotCrash()
    {
        // Arrange
        var service = CreateService(timeoutInSeconds: 10);
        using var cts = new CancellationTokenSource();

        // Starting service in background
        var executeTask = service.StartAsync(cts.Token);

        // Settings mock that first record it call error
        _repositoryMock.SetupSequence(r => r.InsertBatchAsync(It.IsAny<IReadOnlyList<LogEntry>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Database is down"))
            .ReturnsAsync(1);

    }
}
