using System.Threading.Channels;
using LogCollector.Core.Domain;

namespace LogCollector.Infrastructure.Pipeline;

/// <summary>
/// Результат постановки записи в очередь приёма.
/// </summary>
public enum EnqueueResult
{
    /// <summary>Запись принята, ничего не вытеснено.</summary>
    Accepted,

    /// <summary>
    /// Очередь была полна: старейшая запись вытеснена (и её RawBuffer освобождён),
    /// новая запись помещена. Политика «freshest wins» для UDP-мониторинга.
    /// </summary>
    DroppedOldest,
}

/// <summary>
/// Owning-очередь приёма между listener и BatchWriterService.
///
/// <para>
/// Существует потому, что <see cref="Channel{T}"/> с
/// <c>BoundedChannelFullMode.DropOldest</c> вытесняет элемент, НЕ вызывая
/// <c>Dispose()</c> на его <c>IMemoryOwner&lt;byte&gt;</c> — Channel не знает
/// про ownership. Под нагрузкой это ведёт к утечке pool-буферов (дефект P0.1).
/// </para>
///
/// <para>
/// <see cref="TryEnqueue"/> реализует drop-oldest ЯВНО: при заполнении
/// извлекает старейшую запись, освобождает её буфер ровно один раз,
/// увеличивает <see cref="DroppedCount"/> и помещает новую.
/// </para>
/// </summary>
public interface IOwnedIngress
{
    /// <summary>
    /// Помещает запись. При заполнении вытесняет старейшую (с Dispose её буфера).
    /// Атомарна относительно других вызовов TryEnqueue.
    /// </summary>
    EnqueueResult TryEnqueue(LogEntry entry);

    /// <summary>Reader для consumer (BatchWriterService).</summary>
    ChannelReader<LogEntry> Reader { get; }

    /// <summary>
    /// Сигнализирует, что новых записей не будет (shutdown).
    /// Consumer, дочитав остаток, увидит завершение канала.
    /// </summary>
    void Complete();

    /// <summary>Сколько записей вытеснено по drop-oldest. Метрика P2.</summary>
    long DroppedCount { get; }
}
