using System.Buffers;
using System.Threading.Channels;
using LogCollector.Application.Interfaces;
using LogCollector.Application.Pipeline;
using LogCollector.Core.Domain;
using Xunit;

namespace LogCollector.Tests.Pipeline;

public sealed class BatchProcessorTests
{
    [Fact]
    public async Task WritesFullAndPartialBatchesAndReleasesBuffers()
    {
        var repository = new RecordingRepository();
        var processor = new BatchProcessor(repository, new BatchProcessorOptions
        {
            BatchSize = 2,
            FlushInterval = TimeSpan.FromSeconds(1),
            RetryDelay = TimeSpan.FromMilliseconds(1),
        });
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
        Assert.All(owners, owner => Assert.True(owner.IsDisposed));
    }

    [Fact]
    public async Task RetriesFailedBatchWithoutReleasingItEarly()
    {
        var repository = new RecordingRepository(failures: 1);
        var owner = new TrackingOwner();
        var processor = new BatchProcessor(repository, new BatchProcessorOptions
        {
            BatchSize = 1,
            FlushInterval = TimeSpan.FromSeconds(1),
            RetryDelay = TimeSpan.FromMilliseconds(1),
        });
        Channel<LogEntry> channel = Channel.CreateUnbounded<LogEntry>();
        await channel.Writer.WriteAsync(Entry(owner));
        channel.Writer.Complete();

        await processor.RunAsync(channel.Reader, CancellationToken.None);

        Assert.Equal(2, repository.SaveAttempts);
        Assert.True(owner.IsDisposed);
    }

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

    private sealed class RecordingRepository(int failures = 0) : ILogRepository
    {
        private int _failures = failures;
        public bool Initialized { get; private set; }
        public int SaveAttempts { get; private set; }
        public List<int> BatchSizes { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            Initialized = true;
            return Task.CompletedTask;
        }

        public Task SaveBatchAsync(IReadOnlyList<LogEntry> entries, CancellationToken cancellationToken)
        {
            SaveAttempts++;
            if (_failures-- > 0)
                throw new InvalidOperationException("Transient failure");
            BatchSizes.Add(entries.Count);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TrackingOwner : IMemoryOwner<byte>
    {
        public Memory<byte> Memory { get; } = new byte[1];
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
    }
}
