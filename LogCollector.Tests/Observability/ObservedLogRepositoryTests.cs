using LogCollector.Application.Interfaces;
using LogCollector.Core.Domain;
using LogCollector.Infrastructure.Observability;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LogCollector.Tests.Observability;

public sealed class ObservedLogRepositoryTests
{
    [Fact]
    public async Task ObserverFailureDoesNotFailOrRepeatPrimaryWrite()
    {
        var primary = new PrimaryRepository();
        var repository = new ObservedLogRepository(
            primary,
            [new FailingObserver()],
            NullLogger<ObservedLogRepository>.Instance);

        await repository.SaveBatchAsync([default], CancellationToken.None);

        Assert.Equal(1, primary.SaveCount);
    }

    [Fact]
    public async Task ObserverCancellationAfterPrimaryCommitDoesNotRepeatOrFailPrimaryWrite()
    {
        using var cancellation = new CancellationTokenSource();
        var primary = new PrimaryRepository();
        var repository = new ObservedLogRepository(
            primary,
            [new CancellingObserver(cancellation)],
            NullLogger<ObservedLogRepository>.Instance);

        await repository.SaveBatchAsync([default], cancellation.Token);

        Assert.Equal(1, primary.SaveCount);
        Assert.True(cancellation.IsCancellationRequested);
    }

    private sealed class PrimaryRepository : ILogRepository
    {
        public int SaveCount { get; private set; }
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveBatchAsync(IReadOnlyList<LogEntry> entries, CancellationToken cancellationToken)
        {
            SaveCount++;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CancellingObserver(CancellationTokenSource cancellation) : ILogObserver
    {
        public Task PublishBatchAsync(
            IReadOnlyList<LogEntry> entries,
            CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }
    }

    private sealed class FailingObserver : ILogObserver
    {
        public Task PublishBatchAsync(
            IReadOnlyList<LogEntry> entries,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("Loki unavailable");
    }
}
