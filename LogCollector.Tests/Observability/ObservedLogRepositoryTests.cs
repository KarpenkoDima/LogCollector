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

    private sealed class FailingObserver : ILogObserver
    {
        public Task PublishBatchAsync(
            IReadOnlyList<LogEntry> entries,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("Loki unavailable");
    }
}
