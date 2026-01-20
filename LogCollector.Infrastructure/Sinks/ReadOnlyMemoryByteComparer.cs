using System.Diagnostics.CodeAnalysis;

namespace LogCollector.Infrastructure.Sinks
{
    /// <summary>
    /// Allocation-free equality/hashing over ReadOnlyMemory[byte], comparing by content.
    /// 
    /// Used as the Dictionary key comparer in LokiLogSink to group log entries by hostname
    /// without converting Hostname bytes to strings on every comparison.
    /// </summary>
    public sealed class ReadOnlyMemoryByteComparer : IEqualityComparer<ReadOnlyMemory<byte>>
    {
        public static readonly ReadOnlyMemoryByteComparer Instance = new ReadOnlyMemoryByteComparer();
        public bool Equals(ReadOnlyMemory<byte> x, ReadOnlyMemory<byte> y)
            => x.Span.SequenceEqual(y.Span);

        public int GetHashCode([DisallowNull] ReadOnlyMemory<byte> obj)
        {
            var hash = new HashCode();
            hash.AddBytes(obj.Span);
            return hash.ToHashCode();
        }
    }
}
