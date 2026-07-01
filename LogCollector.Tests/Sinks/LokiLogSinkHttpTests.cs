using System.Buffers;
using System.Net;
using System.Text;
using LogCollector.Core.Domain;
using LogCollector.Infrastructure.Sinks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LogCollector.Tests.Sinks;

/// <summary>
/// P1.2 — HTTP lifetime и timeout для LokiLogSink.
///
/// Требования аудита (P1.2):
///   1. Освобождать каждый HttpResponseMessage (проверяем через FakeHttpHandler)
///   2. Единственный HttpClient на lifetime sink (документировано)
///   3. Timeout запроса меньше shutdown timeout
///   4. Ограниченный retry: сетевые/5xx повторяем, 4xx — нет; с cancellation
///   5. Не ловить OperationCanceledException как ошибку доступности
///
/// Используется FakeHttpHandler (аудит прямо просит «fake Loki HTTP handler»),
/// а не реальный Loki — тесты детерминированы и не требуют сети.
/// </summary>
public sealed class LokiLogSinkHttpTests
{
    // ── 1. Успех: один POST, response освобождён ──────────────────────────────

    [Fact]
    public async Task Push_Success_MakesOneRequest_AndDisposesResponse()
    {
        var handler = new FakeHttpHandler(_ => Respond(HttpStatusCode.NoContent));
        var sink = NewSink(handler);

        await sink.SaveBatchAsync(OneEntry(), CancellationToken.None);

        Assert.Equal(1, handler.RequestCount);          // одна попытка
        Assert.Equal(1, handler.DisposedResponses);      // response освобождён
    }

    // ── 2. 4xx — НЕ retry (permanent) ─────────────────────────────────────────

    [Fact]
    public async Task Push_4xx_DoesNotRetry_AndDoesNotThrow()
    {
        // 400 = битый payload. Повтор не поможет — тот же запрос даст ту же 400.
        var handler = new FakeHttpHandler(_ => Respond(HttpStatusCode.BadRequest));
        var sink = NewSink(handler);

        // Secondary best-effort: не бросаем (FanOut всё равно проглотит),
        // но и НЕ повторяем 4xx.
        await sink.SaveBatchAsync(OneEntry(), CancellationToken.None);

        Assert.Equal(1, handler.RequestCount);   // ровно одна попытка, без retry
    }

    // ── 3. 5xx потом 200 — retry сработал ─────────────────────────────────────

    [Fact]
    public async Task Push_5xxThenSuccess_RetriesAndSucceeds()
    {
        int call = 0;
        var handler = new FakeHttpHandler(_ =>
        {
            call++;
            return Respond(call == 1
                ? HttpStatusCode.ServiceUnavailable   // 503 на первой
                : HttpStatusCode.NoContent);           // 204 на второй
        });
        var sink = NewSink(handler);

        await sink.SaveBatchAsync(OneEntry(), CancellationToken.None);

        Assert.Equal(2, handler.RequestCount);           // retry произошёл
        Assert.Equal(2, handler.DisposedResponses);       // ОБА response освобождены
    }

    // ── 4. 5xx всегда — исчерпал retry, не бросил ─────────────────────────────

    [Fact]
    public async Task Push_5xxAlways_ExhaustsRetries_DoesNotThrow()
    {
        var handler = new FakeHttpHandler(_ => Respond(HttpStatusCode.BadGateway));
        var sink = NewSink(handler);

        // Best-effort secondary: даже исчерпав retry, не бросаем наверх —
        // batch уже сохранён primary (SQLite). Loki-copy потеряна, это ок.
        await sink.SaveBatchAsync(OneEntry(), CancellationToken.None);

        // maxRetries=2 → 3 попытки всего (1 + 2 retry)
        Assert.Equal(3, handler.RequestCount);
        Assert.Equal(3, handler.DisposedResponses);   // все три освобождены
    }

    // ── 5. Отмена по ct — проброшена, НЕ проглочена как ошибка ────────────────

    [Fact]
    public async Task Push_CancelledByToken_ThrowsOperationCanceled_NotSwallowed()
    {
        // Handler висит; токен отменяем — это shutdown/бюджет, не ошибка Loki.
        var handler = new FakeHttpHandler(async ct =>
        {
            await Task.Delay(10_000, ct);   // висит, пока ct не отменит
            return Respond(HttpStatusCode.NoContent);
        });
        var sink = NewSink(handler);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(100);

        // Отмена по внешнему ct должна ПРОБРАСЫВАТЬСЯ, не глотаться как
        // «Loki недоступен». Аудит: не ловить OperationCanceledException
        // как обычную ошибку доступности.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sink.SaveBatchAsync(OneEntry(), cts.Token));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static LokiLogSink NewSink(FakeHttpHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://loki:3100") };
        var labels = new Dictionary<string, string> { ["app"] = "logcollector" };
        return new LokiLogSink(http, labels, NullLogger<LokiLogSink>.Instance);
    }

    private static HttpResponseMessage Respond(HttpStatusCode code)
        => new(code) { Content = new StringContent("") };

    private static IReadOnlyList<LogEntry> OneEntry() =>
        new[]
        {
            new LogEntry
            {
                Priority     = 30,
                TimestampRaw = B("Jun  4 18:00:00"),
                Hostname     = B("fw"),
                Topic        = B("firewall"),
                Severity     = SyslogSeverity.Info,
                Message      = B("hello"),
                ReceivedAt   = DateTimeOffset.UtcNow,
                RawBuffer    = null,
            }
        };

    private static ReadOnlyMemory<byte> B(string s) => Encoding.UTF8.GetBytes(s).AsMemory();

    // ── Fake HTTP handler ─────────────────────────────────────────────────────

    /// <summary>
    /// Поддельный HttpMessageHandler для тестов без реального Loki.
    /// Считает запросы и отслеживает, что каждый response был Dispose'нут
    /// (через TrackingResponse). Аудит: «fake Loki HTTP server/handler».
    /// </summary>
    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly Func<CancellationToken, Task<HttpResponseMessage>> _responder;
        private int _requestCount;
        private int _disposedResponses;

        public FakeHttpHandler(Func<CancellationToken, HttpResponseMessage> responder)
            => _responder = ct => Task.FromResult(responder(ct));

        public FakeHttpHandler(Func<CancellationToken, Task<HttpResponseMessage>> responder)
            => _responder = responder;

        public int RequestCount => Volatile.Read(ref _requestCount);
        public int DisposedResponses => Volatile.Read(ref _disposedResponses);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            var inner = await _responder(cancellationToken);

            // Оборачиваем в TrackingResponse, чтобы засечь Dispose.
            return new TrackingResponse(inner, () => Interlocked.Increment(ref _disposedResponses));
        }

        private sealed class TrackingResponse : HttpResponseMessage
        {
            private readonly Action _onDispose;
            public TrackingResponse(HttpResponseMessage src, Action onDispose)
            {
                StatusCode = src.StatusCode;
                Content    = src.Content;
                _onDispose = onDispose;
            }
            protected override void Dispose(bool disposing)
            {
                _onDispose();
                base.Dispose(disposing);
            }
        }
    }
}
