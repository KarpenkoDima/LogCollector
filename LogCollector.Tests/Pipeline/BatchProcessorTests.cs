using System.Buffers;
using System.Threading.Channels;
using LogCollector.Application.Interfaces;
using LogCollector.Application.Pipeline;
using LogCollector.Core.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LogCollector.Tests.Pipeline;

public sealed class BatchProcessorTests
{
    [Fact]
    public async Task WritesFullAndPartialBatchesAndReleasesBuffers()
    {
        var repository = new RecordingRepository();
        var processor = CreateProcessor(repository, batchSize: 2);
        Channel<LogEntry> channel = Channel.CreateBounded<LogEntry>(4);
        var owners = new[] { new TrackingOwner(), new TrackingOwner(), new TrackingOwner() };
        foreach (TrackingOwner owner in owners)
            Assert.True(channel.Writer.TryWrite(Entry(owner)));
        channel.Writer.Complete();

        await processor.RunAsync(channel.Reader, CancellationToken.None);

        Assert.True(repository.Initialized);
        Assert.Collection(repository.BatchSizes,
            size => Assert.Equal(2, size),
            size => Assert.Equal(1, size));
        Assert.All(owners, owner => Assert.Equal(1, owner.DisposeCount));
    }

    [Fact]
    public async Task RetriesTransientFailureWithoutReleasingBatchEarly()
    {
        var repository = new RecordingRepository(transientFailures: 1);
        var owner = new TrackingOwner();
        var processor = CreateProcessor(repository, batchSize: 1);
        Channel<LogEntry> channel = Channel.CreateUnbounded<LogEntry>();
        await channel.Writer.WriteAsync(Entry(owner));
        channel.Writer.Complete();

        await processor.RunAsync(channel.Reader, CancellationToken.None);

        Assert.Equal(2, repository.SaveAttempts);
        Assert.Equal(1, repository.SavedEntries);
        Assert.Equal(1, owner.DisposeCount);
    }

    [Fact]
    public async Task ShutdownWhileWaitingFlushesPartialBatch()
    {
        var repository = new RecordingRepository();
        var processor = CreateProcessor(repository, batchSize: 10, flushInterval: TimeSpan.FromMinutes(1));
        Channel<LogEntry> channel = Channel.CreateBounded<LogEntry>(4);
        var owners = new[] { new TrackingOwner(), new TrackingOwner(), new TrackingOwner() };
        foreach (TrackingOwner owner in owners)
            await channel.Writer.WriteAsync(Entry(owner));

        using var cancellation = new CancellationTokenSource();
        Task run = processor.RunAsync(channel.Reader, cancellation.Token);
        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(3, repository.SavedEntries);
        Assert.All(owners, owner => Assert.Equal(1, owner.DisposeCount));
    }

    [Fact]
    public async Task ShutdownDuringActiveWriteRetriesBatchWithIndependentDeadline()
    {
        var repository = new CancellationOnFirstAttemptRepository();
        var processor = CreateProcessor(repository, batchSize: 1);
        Channel<LogEntry> channel = Channel.CreateUnbounded<LogEntry>();
        var owner = new TrackingOwner();
        await channel.Writer.WriteAsync(Entry(owner));

        using var cancellation = new CancellationTokenSource();
        Task run = processor.RunAsync(channel.Reader, cancellation.Token);
        await repository.FirstAttemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, repository.SaveAttempts);
        Assert.Equal(1, repository.SavedEntries);
        Assert.Equal(1, owner.DisposeCount);
    }

    [Fact]
    public async Task PermanentFailureIsNotRetriedAndReleasesOwnedBuffer()
    {
        var repository = new RecordingRepository(failPermanently: true);
        var owner = new TrackingOwner();
        var processor = CreateProcessor(repository, batchSize: 1);
        Channel<LogEntry> channel = Channel.CreateUnbounded<LogEntry>();
        await channel.Writer.WriteAsync(Entry(owner));
        channel.Writer.Complete();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => processor.RunAsync(channel.Reader, CancellationToken.None));

        Assert.Equal(1, repository.SaveAttempts);
        Assert.Equal(1, owner.DisposeCount);
    }

    [Fact]
    public async Task ExhaustedTransientRetriesAreBoundedAndReleaseOwnedBuffer()
    {
        var repository = new RecordingRepository(transientFailures: 10);
        var owner = new TrackingOwner();
        var processor = CreateProcessor(repository, batchSize: 1, maxAttempts: 3);
        Channel<LogEntry> channel = Channel.CreateUnbounded<LogEntry>();
        await channel.Writer.WriteAsync(Entry(owner));
        channel.Writer.Complete();

        await Assert.ThrowsAsync<TransientLogPersistenceException>(
            () => processor.RunAsync(channel.Reader, CancellationToken.None));

        Assert.Equal(3, repository.SaveAttempts);
        Assert.Equal(1, owner.DisposeCount);
    }

    private static BatchProcessor CreateProcessor(
        ILogRepository repository,
        int batchSize,
        TimeSpan? flushInterval = null,
        int maxAttempts = 5) =>
        new(repository, new BatchProcessorOptions
        {
            BatchSize = batchSize,
            FlushInterval = flushInterval ?? TimeSpan.FromSeconds(1),
            RetryDelay = TimeSpan.FromMilliseconds(1),
            MaxAttempts = maxAttempts,
            ShutdownFlushTimeout = TimeSpan.FromSeconds(1),
        }, NullLogger<BatchProcessor>.Instance);

    private static LogEntry Entry(IMemoryOwner<byte> owner) => new()
    {
        Priority = 30,
        Timestamp = ReadOnlyMemory<byte>.Empty,
        Hostname = ReadOnlyMemory<byte>.Empty,
        Topic = ReadOnlyMemory<byte>.Empty,
        Message = ReadOnlyMemory<byte>.Empty,
        ReceivedAt = DateTimeOffset.UtcNow,
        BufferOwner = owner,
    };

    private sealed class RecordingRepository(int transientFailures = 0, bool failPermanently = false)
        : ILogRepository
    {
        private int _remainingTransientFailures = transientFailures;
        public bool Initialized { get; private set; }
        public int SaveAttempts { get; private set; }
        public int SavedEntries { get; private set; }
        public List<int> BatchSizes { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            Initialized = true;
            return Task.CompletedTask;
        }

        public Task SaveBatchAsync(IReadOnlyList<LogEntry> entries, CancellationToken cancellationToken)
        {
            SaveAttempts++;
            if (failPermanently)
                throw new InvalidOperationException("Permanent failure");

            if (_remainingTransientFailures > 0)
            {
                _remainingTransientFailures--;
                throw new TransientLogPersistenceException(
                    "Transient failure", new IOException("SQLite busy"));
            }

            BatchSizes.Add(entries.Count);
            SavedEntries += entries.Count;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CancellationOnFirstAttemptRepository : ILogRepository
    {
        private int _saveAttempts;
        public TaskCompletionSource<bool> FirstAttemptStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SaveAttempts => _saveAttempts;
        public int SavedEntries { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task SaveBatchAsync(
            IReadOnlyList<LogEntry> entries,
            CancellationToken cancellationToken)
        {
            int attempt = Interlocked.Increment(ref _saveAttempts);
            if (attempt == 1)
            {
                FirstAttemptStarted.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            SavedEntries += entries.Count;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TrackingOwner : IMemoryOwner<byte>
    {
        public Memory<byte> Memory { get; } = new byte[1];
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }
}
