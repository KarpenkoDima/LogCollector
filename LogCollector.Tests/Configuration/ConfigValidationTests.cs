using LogCollector.Infrastructure.Listeners;
using LogCollector.Infrastructure.Pipeline;
using Microsoft.Extensions.Options;
using Xunit;

namespace LogCollector.Tests.Configuration;

/// <summary>
/// P1.3 — валидация конфигурации при запуске.
///
/// Валидаторы (IValidateOptions) проверяют диапазоны ДО приёма трафика.
/// ValidateOnStart в DI поднимает эти проверки на момент старта хоста, так что
/// битый конфиг роняет сервис сразу с понятной ошибкой, а не молча позже.
///
/// Требования аудита P1.3:
///   Port ∈ [1, 65535], MaxDatagramSize ∈ [256, 65507],
///   BatchSize ∈ [1, 10000], BatchTimeout > 0 и ≤ 30s.
/// </summary>
public sealed class ConfigValidationTests
{
    // ── SyslogListenerOptions ─────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]        // ниже диапазона
    [InlineData(-1)]       // отрицательный
    [InlineData(65_536)]   // выше 65535
    public void ListenerValidator_RejectsPortOutOfRange(int port)
    {
        var v = new SyslogListenerOptionsValidator();
        var opts = new SyslogListenerOptions { Port = port, MaxDatagramSize = 8192 };

        var result = v.Validate(null, opts);

        Assert.True(result.Failed, $"Port {port} должен быть отклонён");
    }

    [Theory]
    [InlineData(255)]      // ниже 256
    [InlineData(65_508)]   // выше 65507
    public void ListenerValidator_RejectsDatagramSizeOutOfRange(int size)
    {
        var v = new SyslogListenerOptionsValidator();
        var opts = new SyslogListenerOptions { Port = 5140, MaxDatagramSize = size };

        var result = v.Validate(null, opts);

        Assert.True(result.Failed, $"MaxDatagramSize {size} должен быть отклонён");
    }

    [Fact]
    public void ListenerValidator_AcceptsValidValues()
    {
        var v = new SyslogListenerOptionsValidator();
        var opts = new SyslogListenerOptions { Port = 5140, MaxDatagramSize = 8192 };

        var result = v.Validate(null, opts);

        Assert.True(result.Succeeded);
    }

    // ── BatchWriterOptions ────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]        // ниже 1
    [InlineData(-5)]       // отрицательный
    [InlineData(10_001)]   // выше 10000
    public void BatchValidator_RejectsBatchSizeOutOfRange(int size)
    {
        var v = new BatchWriterOptionsValidator();
        var opts = new BatchWriterOptions
        {
            BatchSize = size,
            BatchTimeout = TimeSpan.FromSeconds(2),
        };

        var result = v.Validate(null, opts);

        Assert.True(result.Failed, $"BatchSize {size} должен быть отклонён");
    }

    [Theory]
    [InlineData(0)]        // ноль — не больше нуля
    [InlineData(-1)]       // отрицательный
    [InlineData(31)]       // выше 30 секунд
    public void BatchValidator_RejectsBatchTimeoutOutOfRange(int seconds)
    {
        var v = new BatchWriterOptionsValidator();
        var opts = new BatchWriterOptions
        {
            BatchSize = 500,
            BatchTimeout = TimeSpan.FromSeconds(seconds),
        };

        var result = v.Validate(null, opts);

        Assert.True(result.Failed, $"BatchTimeout {seconds}s должен быть отклонён");
    }

    [Fact]
    public void BatchValidator_AcceptsValidValues()
    {
        var v = new BatchWriterOptionsValidator();
        var opts = new BatchWriterOptions
        {
            BatchSize = 500,
            BatchTimeout = TimeSpan.FromSeconds(2),
        };

        var result = v.Validate(null, opts);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void BatchValidator_AcceptsBoundaryValues()
    {
        var v = new BatchWriterOptionsValidator();

        // Границы: BatchSize=1, BatchSize=10000, timeout=30s — все валидны
        Assert.True(v.Validate(null,
            new BatchWriterOptions { BatchSize = 1, BatchTimeout = TimeSpan.FromSeconds(30) })
            .Succeeded);
        Assert.True(v.Validate(null,
            new BatchWriterOptions { BatchSize = 10_000, BatchTimeout = TimeSpan.FromMilliseconds(1) })
            .Succeeded);
    }
}
