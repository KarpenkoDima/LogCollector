using System.Threading.Channels;
using LogCollector.Core.Domain;

namespace LogCollector.Infrastructure.Pipeline;

/// <summary>
/// Реализация <see cref="IOwnedIngress"/> поверх bounded <see cref="Channel{T}"/>.
///
/// <para><b>Почему внутренний канал в режиме Wait, а не DropOldest:</b></para>
/// <para>
/// Встроенный DropOldest — это ровно тот дефект, который мы обходим: он не
/// диспозит вытесненный owner. Поэтому внутри — <c>FullMode.Wait</c>, а
/// drop-oldest мы реализуем сами в <see cref="TryEnqueue"/>, с явным Dispose.
/// </para>
///
/// <para><b>Синхронизация:</b></para>
/// <para>
/// <see cref="TryEnqueue"/> под <c>lock</c>: сначала неблокирующий TryWrite,
/// при неудаче — извлечь старейший, Dispose, TryWrite снова. Один lock
/// делает последовательность проверка-вытеснение-вставка атомарной, устраняя
/// гонку между несколькими писателями и одновременным вытеснением.
/// </para>
/// <para>
/// Внутренний канал создаётся с <c>SingleReader=false</c>, потому что читают
/// двое: consumer (штатно) и eviction-путь (TryRead при вытеснении).
/// </para>
/// </summary>
public sealed class OwnedIngress : IOwnedIngress
{
    private readonly Channel<LogEntry> _channel;
    private readonly object _enqueueLock = new();
    private long _droppedCount;

    public OwnedIngress(int capacity)
    {
        _channel = Channel.CreateBounded<LogEntry>(
            new BoundedChannelOptions(capacity)
            {
                // Wait, НЕ DropOldest — вытеснение делаем сами с Dispose.
                FullMode     = BoundedChannelFullMode.Wait,
                SingleReader = false, // consumer + eviction оба читают
                SingleWriter = false, // listener + Complete из разных потоков
            });
    }

    public ChannelReader<LogEntry> Reader => _channel.Reader;

    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    public EnqueueResult TryEnqueue(LogEntry entry)
    {
        lock (_enqueueLock)
        {
            // Быстрый путь: есть место — просто пишем.
            if (_channel.Writer.TryWrite(entry))
                return EnqueueResult.Accepted;

            // Канал полон. Вытесняем старейшего: читаем один элемент,
            // освобождаем его буфер РОВНО ОДИН РАЗ, затем пишем новый.
            if (_channel.Reader.TryRead(out var evicted))
            {
                evicted.RawBuffer?.Dispose();       // ← фикс P0.1: явный Dispose
                Interlocked.Increment(ref _droppedCount);
            }

            // После вытеснения место освободилось — запись должна пройти.
            // Если вдруг снова полон (другой писатель под тем же lock невозможен,
            // но consumer мог не успеть) — TryWrite вернёт true в норме.
            if (_channel.Writer.TryWrite(entry))
                return EnqueueResult.DroppedOldest;

            // Крайне маловероятно: не смогли записать даже после вытеснения.
            // Чтобы не потерять владение — диспозим новый буфер и сообщаем drop.
            entry.RawBuffer?.Dispose();
            Interlocked.Increment(ref _droppedCount);
            return EnqueueResult.DroppedOldest;
        }
    }

    public void Complete() => _channel.Writer.TryComplete();
}
