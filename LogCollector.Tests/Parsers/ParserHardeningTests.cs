using System.Text;
using LogCollector.Core.Domain;
using LogCollector.Infrastructure.Parsers;
using Xunit;

namespace LogCollector.Tests.Parsers;

/// <summary>
/// P1.4 — усиление парсера против битых дейтаграмм.
///
/// Дешёвые проверки (аудит P1.4): PRI из цифр, Utf8Parser consumed всё,
/// PRI ∈ [0,191], timestamp 15 байт с разделителями, hostname не пустой,
/// cursor не выходит за source.
///
/// Permissive к неизвестным MikroTik topic/severity — НЕ ужесточаем это.
/// Без regex, без аллокаций строк на hot path.
/// </summary>
public sealed class ParserHardeningTests
{
    private static bool Parse(string line, out LogEntry entry)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line);
        return SyslogParser.TryParse(bytes.AsMemory(), DateTimeOffset.UtcNow, out entry);
    }

    // ── PRI из цифр + consumed всё ────────────────────────────────────────────

    [Theory]
    [InlineData("<12x>Jun  4 18:00:00 fw message")]   // буква в PRI
    [InlineData("<1 2>Jun  4 18:00:00 fw message")]   // пробел в PRI
    [InlineData("<+5>Jun  4 18:00:00 fw message")]    // знак в PRI
    [InlineData("<>Jun  4 18:00:00 fw message")]      // пустой PRI
    public void Rejects_NonNumericOrPartialPri(string line)
    {
        Assert.False(Parse(line, out _),
            "PRI должен полностью состоять из цифр и парситься целиком");
    }

    // ── PRI в диапазоне [0, 191] ──────────────────────────────────────────────

    [Theory]
    [InlineData("<192>Jun  4 18:00:00 fw message")]   // выше 191
    [InlineData("<999>Jun  4 18:00:00 fw message")]   // явно вне диапазона
    [InlineData("<256>Jun  4 18:00:00 fw message")]
    public void Rejects_PriOutOfRange(string line)
    {
        Assert.False(Parse(line, out _),
            "PRI вне [0,191] должен отклоняться");
    }

    [Theory]
    [InlineData("<0>Jun  4 18:00:00 fw message")]     // нижняя граница
    [InlineData("<191>Jun  4 18:00:00 fw message")]   // верхняя граница
    [InlineData("<30>Jun  4 18:00:00 fw message")]    // типичное значение
    public void Accepts_PriInRange(string line)
    {
        Assert.True(Parse(line, out _),
            "PRI в [0,191] должен приниматься");
    }

    // ── Hostname не пустой ────────────────────────────────────────────────────

    [Fact]
    public void Rejects_EmptyHostname()
    {
        // Двойной пробел после timestamp → пустой hostname
        Assert.False(Parse("<30>Jun  4 18:00:00  message", out _),
            "Пустой hostname должен отклоняться");
    }

    // ── Обрезанные/битые дейтаграммы не роняют парсер ─────────────────────────

    [Theory]
    [InlineData("<30>")]                          // только PRI
    [InlineData("<30>Jun")]                       // обрыв на timestamp
    [InlineData("<30>Jun  4 18:00")]              // короткий timestamp
    [InlineData("<30>Jun  4 18:00:00")]           // нет hostname вообще
    [InlineData("<30>Jun  4 18:00:00 ")]          // trailing space, нет hostname
    public void Rejects_TruncatedDatagram_WithoutThrowing(string line)
    {
        // Главное — НЕ бросает исключение (cursor не выходит за source).
        var ex = Record.Exception(() => Parse(line, out _));
        Assert.Null(ex);
    }

    [Fact]
    public void Rejects_GarbageBytes_WithoutThrowing()
    {
        // Случайные байты не должны ронять парсер.
        byte[] garbage = { 0xFF, 0x00, 0x3C, 0x99, 0xAB, 0x20, 0x00 };
        var ex = Record.Exception(() =>
            SyslogParser.TryParse(garbage.AsMemory(), DateTimeOffset.UtcNow, out _));
        Assert.Null(ex);
    }

    // ── Permissive-подход СОХРАНЁН: неизвестный topic/severity не роняет ──────

    [Fact]
    public void Preserves_Permissive_UnknownSeverity()
    {
        // Неизвестный severity → Unknown, но запись принимается (не дропается).
        Assert.True(Parse("<30>Jun  4 18:00:00 fw : system,frobnicate something", out var e));
        Assert.Equal(SyslogSeverity.Unknown, e.Severity);
    }

    [Fact]
    public void Preserves_Permissive_UnknownTopic()
    {
        // Любой topic принимается — MikroTik может слать что угодно.
        Assert.True(Parse("<30>Jun  4 18:00:00 fw : weirdtopic,info msg", out var e));
        Assert.Equal("weirdtopic", Encoding.UTF8.GetString(e.Topic.Span));
    }

    // ── Валидные форматы по-прежнему проходят ─────────────────────────────────

    [Fact]
    public void Accepts_ValidFormatA_MikroTikExtended()
    {
        Assert.True(Parse("<30>Jun  4 18:00:00 mtk-router : firewall,info forward: msg", out var e));
        Assert.Equal("mtk-router", Encoding.UTF8.GetString(e.Hostname.Span));
        Assert.Equal("firewall",   Encoding.UTF8.GetString(e.Topic.Span));
        Assert.Equal(SyslogSeverity.Info, e.Severity);
    }

    [Fact]
    public void Accepts_ValidFormatB_Rfc3164()
    {
        Assert.True(Parse("<30>Jun 18 20:50:28 MikroTikHome filter rule changed", out var e));
        Assert.Equal("MikroTikHome", Encoding.UTF8.GetString(e.Hostname.Span));
        Assert.True(e.Topic.IsEmpty);
    }
}
