using System.Buffers;
using System.Text;
using System.Threading.Channels;
using LogCollector.Application.Interfaces;
using LogCollector.Core.Domain;
using LogCollector.Infrastructure.Pipeline;
using LogCollector.Infrastructure.Sinks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LogCollector.Tests.Pipeline;

/// <summary>
/// Этап 0 из production-readiness аудита (14 июля 2026): воспроизвести
/// два P0-дефекта КРАСНЫМИ тестами ПЕРЕД тем как чинить.
///
/// Эти тесты ДОЛЖНЫ падать на текущем коде. Их падение — доказательство,
/// что дефект реален. После фиксов P0.1 и P0.2 они станут зелёными и
/// зафиксируют, что дефект закрыт и не вернётся.
///
/// НЕ УДАЛЯТЬ эти тесты после фикса — они становятся regression guard.
/// </summary>
public sealed class ProductionReadinessTests
{
    // ══════════════════════════════════════════════════════════════════════════
    // P0.1 — DropOldest вытесняет LogEntry, не освобождая IMemoryOwner<byte>
    // ══════════════════════════════════════════════════════════════════════════
    //
    // Дефект принадлежит САМОМУ каналу с FullMode=DropOldest, а не
    // BatchWriterService. Вытеснение происходит внутри WriteAsync, когда
    // канал полон. Поэтому тест работает с голым каналом, без сервиса —
    // это исключает любые гонки и делает падение детерминированным.

    [Fact]
    public void OwnedIngress_EvictsOldest_AndDisposesItsBuffer_ExactlyOnce()
    {
        // Arrange: OwnedIngress ёмкости 1. Ёмкость 1 делает переполнение
        // детерминированным на второй записи. Reader НЕ подключён — иначе
        // он вычитает A раньше вытеснения.
        var ingress = new OwnedIngress(capacity: 1);

        var evictedOwner   = new TrackingOwner();  // owner записи, которую вытеснят
        var survivingOwner = new TrackingOwner();  // owner записи, которая останется

        // Act: A занимает единственный слот, B вытесняет A.
        var r1 = ingress.TryEnqueue(EntryWith(evictedOwner));
        var r2 = ingress.TryEnqueue(EntryWith(survivingOwner));

        // Assert: первая принята без вытеснения, вторая вытеснила старейшую.
        Assert.Equal(EnqueueResult.Accepted, r1);
        Assert.Equal(EnqueueResult.DroppedOldest, r2);

        // Ключевой assert P0.1: вытеснённый owner освобождён (фикс работает).
        Assert.True(evictedOwner.IsDisposed,
            "P0.1: вытеснённый по drop-oldest LogEntry должен освободить RawBuffer.");

        // Счётчик drops увеличился ровно на один.
        Assert.Equal(1, ingress.DroppedCount);

        // Выжившая запись НЕ освобождена — она всё ещё в очереди.
        Assert.False(survivingOwner.IsDisposed,
            "Выжившая запись не должна быть освобождена — она всё ещё в очереди.");

        // Дочитываем выжившую и проверяем, что это именно B (survivingOwner).
        Assert.True(ingress.Reader.TryRead(out var remaining));
        Assert.Same(survivingOwner, remaining.RawBuffer);
    }

    [Fact]
    public void OwnedIngress_NoOverflow_DoesNotDisposeAnything()
    {
        // Sanity: если переполнения нет, ничего не диспозится и drops=0.
        var ingress = new OwnedIngress(capacity: 4);

        var o1 = new TrackingOwner();
        var o2 = new TrackingOwner();

        Assert.Equal(EnqueueResult.Accepted, ingress.TryEnqueue(EntryWith(o1)));
        Assert.Equal(EnqueueResult.Accepted, ingress.TryEnqueue(EntryWith(o2)));

        Assert.False(o1.IsDisposed);
        Assert.False(o2.IsDisposed);
        Assert.Equal(0, ingress.DroppedCount);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // P0.2 — BatchWriterService сливает snapshot канала, а не накапливает окно
    // ══════════════════════════════════════════════════════════════════════════
    //
    // Аудит, критерий готовности: "8 быстро отправленных записей при
    // BatchSize=8 дают один repository call размером 8".
    //
    // Текущий WaitToReadAsync + TryRead просыпается на первой записи и
    // сливает то, что УЖЕ в канале. Если записи приходят не мгновенно
    // (реальный трафик всегда такой), получится несколько мелких batch-ей
    // вместо одного из 8.

    [Fact]
    public async Task Batching_EightRapidEntries_ShouldProduceExactlyOneBatchOfEight()
    {
        // Arrange: BatchSize=8, большой timeout чтобы окно точно не истекло.
        var channel = Channel.CreateBounded<LogEntry>(1_000);
        var repo    = new CountingRepository();
        var opts    = Options.Create(new BatchWriterOptions
        {
            BatchSize    = 8,
            BatchTimeout = TimeSpan.FromSeconds(5),  // намного больше окна теста
        });
        var svc = new BatchWriterService(
            channel.Reader, repo, opts, NullLogger<BatchWriterService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await svc.StartAsync(cts.Token);

        // Act: 8 записей с микрозадержкой 1мс — имитация реального сетевого
        // трафика, где дейтаграммы приходят не в одну и ту же наносекунду.
        for (int i = 0; i < 8; i++)
        {
            await channel.Writer.WriteAsync(Entry($"msg {i}"), cts.Token);
            await Task.Delay(1, cts.Token);
        }

        // Даём consumer время обработать
        await Task.Delay(300, cts.Token);
        await svc.StopAsync(cts.Token);

        // Assert 1: должен быть РОВНО ОДИН вызов repository с 8 записями.
        //
        // НА ТЕКУЩЕМ КОДЕ ЭТОТ ASSERT ПАДАЕТ:
        // consumer просыпается на первой записи, сливает 1-2 что успели
        // прийти, сохраняет мелкий batch, повторяет. Получается несколько
        // вызовов (например 1+1+1+... или 2+3+3), а не один из 8.
        Assert.Equal(1, repo.BatchCount);

        // Assert 2: этот единственный batch должен содержать все 8 записей.
        Assert.Equal(8, repo.BatchSizes.Single());

        // Общий диагностический вывод при падении
        Assert.True(repo.BatchCount == 1 && repo.BatchSizes.Single() == 8,
            $"P0.2 ДЕФЕКТ: ожидался 1 batch из 8, получено " +
            $"{repo.BatchCount} batch-ей размеров [{string.Join(",", repo.BatchSizes)}]. " +
            $"Текущий drain сливает snapshot канала, а не накапливает окно " +
            $"от первой записи до BatchSize или BatchTimeout. " +
            $"Фикс: batch стартует с первой записи и закрывается по " +
            $"size ИЛИ timeout ИЛИ completion ИЛИ shutdown.");
    }

    [Fact]
    public async Task Batching_TwentyEntries_ShouldProduceBatchesOfEightEightFour()
    {
        // Аудит, критерий: "20 записей при BatchSize=8 дают batch-и 8, 8 и 4".
        var channel = Channel.CreateBounded<LogEntry>(1_000);
        var repo    = new CountingRepository();
        var opts    = Options.Create(new BatchWriterOptions
        {
            BatchSize    = 8,
            BatchTimeout = TimeSpan.FromSeconds(5),
        });
        var svc = new BatchWriterService(
            channel.Reader, repo, opts, NullLogger<BatchWriterService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await svc.StartAsync(cts.Token);

        for (int i = 0; i < 20; i++)
        {
            await channel.Writer.WriteAsync(Entry($"msg {i}"), cts.Token);
            await Task.Delay(1, cts.Token);
        }

        await Task.Delay(300, cts.Token);
        await svc.StopAsync(cts.Token);

        // Ожидаем последовательность размеров: 8, 8, 4 (всего 20).
        // На текущем коде размеры будут дробными и в другом количестве.
        Assert.Equal(new[] { 8, 8, 4 }, repo.BatchSizes.ToArray());
    }

    // ══════════════════════════════════════════════════════════════════════════
    // P0.3 — Graceful shutdown: хвост сохранён, все owners освобождены ровно раз
    // ══════════════════════════════════════════════════════════════════════════
    //
    // Критерии аудита: listener остановлен первым, ingress завершён, writer
    // сохраняет хвост, после StopAsync outstanding owners == 0.
    // Тесты доказывают критерии 3 и 4 (writer сохраняет хвост + owners=0).

    [Fact]
    public async Task Shutdown_WithGatedRepository_SavesTail_AndDisposesEveryOwnerOnce()
    {
        // Gate-подход: repository блокируется на первом вызове, пока мы не
        // отпустим gate. Это ДЕТЕРМИНИРОВАННО гарантирует, что на момент
        // StopAsync в канале висит необработанный хвост.
        var channel = Channel.CreateBounded<LogEntry>(1_000);
        var gate    = new SemaphoreSlim(0, 1);
        var repo    = new GatedRepository(gate);
        var opts    = Options.Create(new BatchWriterOptions
        {
            BatchSize    = 4,
            BatchTimeout = TimeSpan.FromMilliseconds(50),
        });
        var svc = new BatchWriterService(
            channel.Reader, repo, opts, NullLogger<BatchWriterService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await svc.StartAsync(cts.Token);

        // Пишем 10 записей с отслеживаемыми owner'ами.
        var owners = new List<TrackingOwner>();
        for (int i = 0; i < 10; i++)
        {
            var owner = new TrackingOwner();
            owners.Add(owner);
            await channel.Writer.WriteAsync(EntryWith(owner), cts.Token);
        }

        // Даём consumer'у войти в первый SaveBatchAsync и застрять на gate.
        await Task.Delay(200, cts.Token);

        // Отпускаем gate — consumer сможет обрабатывать. Одновременно
        // инициируем shutdown: часть записей — хвост — ещё в канале.
        gate.Release();
        await svc.StopAsync(cts.Token);

        // Assert 3 (критерий): ВСЕ 10 записей дошли до repo — хвост не потерян.
        Assert.Equal(10, repo.TotalSaved);

        // Assert 4 (критерий): каждый owner освобождён РОВНО ОДИН РАЗ.
        // Ноль → потерян буфер (leak). Два → double-dispose (баг).
        foreach (var owner in owners)
            Assert.Equal(1, owner.DisposeCount);

        // Outstanding owners == 0: сумма недиспозженных.
        Assert.Equal(0, owners.Count(o => o.DisposeCount == 0));
    }

    [Fact]
    public async Task Shutdown_UnderLoad_LosesNothing_AndDisposesEveryOwnerOnce()
    {
        // Нагрузочный подход: непрерывно пишем, пока consumer обрабатывает,
        // затем резко останавливаем. Реалистичнее gate — проверяет, что при
        // живом потоке shutdown не роняет ни одной записи.
        var channel = Channel.CreateBounded<LogEntry>(10_000);
        var repo    = new CountingRepository();
        var opts    = Options.Create(new BatchWriterOptions
        {
            BatchSize    = 16,
            BatchTimeout = TimeSpan.FromMilliseconds(20),
        });
        var svc = new BatchWriterService(
            channel.Reader, repo, opts, NullLogger<BatchWriterService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await svc.StartAsync(cts.Token);

        // Пишем 500 записей потоком с отслеживанием owner'ов.
        const int total = 500;
        var owners = new List<TrackingOwner>(total);
        for (int i = 0; i < total; i++)
        {
            var owner = new TrackingOwner();
            owners.Add(owner);
            await channel.Writer.WriteAsync(EntryWith(owner), cts.Token);
            if (i % 50 == 0)
                await Task.Delay(1, cts.Token);   // лёгкие зазоры, имитация трафика
        }

        // Останавливаем — финальный drain должен сохранить весь хвост.
        await svc.StopAsync(cts.Token);

        // Ничего не потеряно: все 500 записей сохранены.
        var totalSaved = repo.BatchSizes.Sum();
        Assert.Equal(total, totalSaved);

        // Каждый owner освобождён ровно один раз — ни leak, ни double-dispose.
        Assert.Equal(0, owners.Count(o => o.DisposeCount != 1));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // P1.1 — Изоляция SQLite (primary) от Loki/Console (secondary)
    // ══════════════════════════════════════════════════════════════════════════
    //
    // Контракт (аудит P1.1):
    //   1. SQLite упал  → SaveBatchAsync бросает (реальная потеря batch)
    //   2. Loki упал    → SaveBatchAsync успешен (SQLite записал)
    //   3. Loki висит   → SaveBatchAsync не висит дольше secondary-таймаута
    //   4. ошибки secondary НЕ проваливают batch

    [Fact]
    public async Task FanOut_PrimaryFails_ThrowsBatchConsideredLost()
    {
        // SQLite (primary) упал — это реальная потеря, batch провален.
        var primary    = new FakeSink("Sqlite") { Throws = true };
        var secondary  = new FakeSink("Loki");
        var fan = new FanOutLogRepository(
            primary,
            new ILogSink[] { secondary },
            secondaryTimeout: TimeSpan.FromSeconds(1),
            NullLogger<FanOutLogRepository>.Instance);

        // Primary бросил → SaveBatchAsync должен бросить (не проглотить).
        await Assert.ThrowsAnyAsync<Exception>(
            () => fan.SaveBatchAsync(OneEntry(), CancellationToken.None));
    }

    [Fact]
    public async Task FanOut_SecondaryFails_BatchStillSucceeds()
    {
        // Loki (secondary) упал — SQLite записал, значит batch сохранён.
        var primary   = new FakeSink("Sqlite");
        var badLoki   = new FakeSink("Loki") { Throws = true };
        var fan = new FanOutLogRepository(
            primary,
            new ILogSink[] { badLoki },
            secondaryTimeout: TimeSpan.FromSeconds(1),
            NullLogger<FanOutLogRepository>.Instance);

        // Не должно бросить — primary успешен.
        await fan.SaveBatchAsync(OneEntry(), CancellationToken.None);

        Assert.True(primary.WasCalled, "SQLite (primary) должен быть вызван");
        Assert.True(badLoki.WasCalled, "Loki (secondary) должен быть вызван (и упасть тихо)");
    }

    [Fact]
    public async Task FanOut_SecondaryHangs_DoesNotBlockBeyondTimeout()
    {
        // Loki (secondary) висит дольше таймаута — SaveBatchAsync НЕ должен
        // висеть дольше secondaryTimeout + запас.
        var primary    = new FakeSink("Sqlite");
        var hangingLoki = new FakeSink("Loki") { HangMs = 10_000 };  // висит 10 сек
        var fan = new FanOutLogRepository(
            primary,
            new ILogSink[] { hangingLoki },
            secondaryTimeout: TimeSpan.FromMilliseconds(200),
            NullLogger<FanOutLogRepository>.Instance);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await fan.SaveBatchAsync(OneEntry(), CancellationToken.None);
        sw.Stop();

        // Primary мгновенный + secondary таймаут 200мс. Общее время должно
        // быть заметно меньше 10 секунд зависания Loki.
        Assert.True(sw.ElapsedMilliseconds < 2_000,
            $"SaveBatchAsync висел {sw.ElapsedMilliseconds}мс — " +
            $"зависший secondary не должен держать конвейер дольше таймаута.");

        Assert.True(primary.WasCalled);
    }

    [Fact]
    public async Task FanOut_PrimarySucceeds_SecondaryTimesOut_BatchNotLost()
    {
        // Комбинация: primary ок, secondary в таймаут — batch НЕ потерян.
        var primary     = new FakeSink("Sqlite");
        var slowLoki    = new FakeSink("Loki") { HangMs = 5_000 };
        var fan = new FanOutLogRepository(
            primary,
            new ILogSink[] { slowLoki },
            secondaryTimeout: TimeSpan.FromMilliseconds(100),
            NullLogger<FanOutLogRepository>.Instance);

        // Не бросает — primary записал, secondary-таймаут это best-effort.
        await fan.SaveBatchAsync(OneEntry(), CancellationToken.None);
        Assert.True(primary.WasCalled);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static LogEntry EntryWith(IMemoryOwner<byte> owner) =>
        new()
        {
            Priority     = 30,
            TimestampRaw = Bytes("Jun  4 18:00:00"),
            Hostname     = Bytes("fw"),
            Topic        = Bytes("firewall"),
            Severity     = SyslogSeverity.Info,
            Message      = Bytes("x"),
            ReceivedAt   = DateTimeOffset.UtcNow,
            RawBuffer    = owner,
        };

    private static LogEntry Entry(string message) =>
        new()
        {
            Priority     = 30,
            TimestampRaw = Bytes("Jun  4 18:00:00"),
            Hostname     = Bytes("fw"),
            Topic        = Bytes("firewall"),
            Severity     = SyslogSeverity.Info,
            Message      = Bytes(message),
            ReceivedAt   = DateTimeOffset.UtcNow,
            RawBuffer    = null,
        };

    private static IReadOnlyList<LogEntry> OneEntry() =>
        new[] { Entry("payload") };

    private static ReadOnlyMemory<byte> Bytes(string s)
        => Encoding.UTF8.GetBytes(s).AsMemory();

    // ── Test doubles ─────────────────────────────────────────────────────────

    /// <summary>
    /// Repository, который считает вызовы SaveBatchAsync и запоминает размер
    /// каждого batch. Нужен для P0.2 — существующий InMemoryLogRepository
    /// считает только суммарные записи, но не число и размеры batch-ей.
    /// </summary>
    private sealed class CountingRepository : ILogRepository
    {
        private readonly object _lock = new();
        public List<int> BatchSizes { get; } = new();
        public int BatchCount { get { lock (_lock) return BatchSizes.Count; } }

        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

        public Task SaveBatchAsync(IReadOnlyList<LogEntry> batch, CancellationToken ct)
        {
            lock (_lock) BatchSizes.Add(batch.Count);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Repository, который блокируется на первом SaveBatchAsync до release gate.
    /// Гарантирует детерминированное наличие необработанного хвоста на StopAsync.
    /// </summary>
    private sealed class GatedRepository : ILogRepository
    {
        private readonly SemaphoreSlim _gate;
        private readonly object _lock = new();
        private bool _gatePassed;
        private int _totalSaved;

        public GatedRepository(SemaphoreSlim gate) => _gate = gate;

        public int TotalSaved { get { lock (_lock) return _totalSaved; } }

        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

        public async Task SaveBatchAsync(IReadOnlyList<LogEntry> batch, CancellationToken ct)
        {
            // Первый вызов ждёт gate — держит consumer, пока копится хвост.
            // CancellationToken.None в финальном drain: gate уже отпущен к тому
            // моменту, так что блокировки при shutdown не будет.
            bool needGate;
            lock (_lock) { needGate = !_gatePassed; _gatePassed = true; }
            if (needGate)
                await _gate.WaitAsync(CancellationToken.None);

            lock (_lock) _totalSaved += batch.Count;
        }
    }

    /// <summary>
    /// Настраиваемый ILogSink для тестов изоляции: может бросать, висеть,
    /// и запоминает факт вызова. Name задаётся явно для проверки логирования.
    /// </summary>
    private sealed class FakeSink : ILogSink
    {
        public FakeSink(string name) => Name = name;

        public string Name { get; }
        public bool Throws { get; init; }
        public int HangMs { get; init; }
        public volatile bool WasCalled;

        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

        public async Task SaveBatchAsync(IReadOnlyList<LogEntry> batch, CancellationToken ct)
        {
            WasCalled = true;
            if (HangMs > 0)
                await Task.Delay(HangMs, ct);   // ct отменит по secondary-таймауту
            if (Throws)
                throw new InvalidOperationException($"{Name} simulated failure");
        }
    }

    private sealed class TrackingOwner : IMemoryOwner<byte>
    {
        private int _disposeCount;
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public bool IsDisposed => DisposeCount > 0;
        public Memory<byte> Memory { get; } = new byte[4];

        // Interlocked: Dispose может вызываться из разных потоков
        // (consumer, eviction, drain) — считаем безопасно.
        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}
