using LogCollector.Application.Interfaces;
using LogCollector.Core.Domain;

namespace LogCollector.Infrastructure.Parsers;

/// <summary>
/// Tries each registered <see cref="ILogParser"/> in order and returns
/// the result of the first one that succeeds.
///
/// <para>
/// This is the single <see cref="ILogParser"/> that <c>UdpSyslogListener</c>
/// receives.  Adding a new format requires zero changes to the listener:
/// register the new parser in <c>ServiceCollectionExtensions</c> and pass it
/// to this composite.
/// </para>
///
/// <para><b>Parser ordering matters.</b>  Formats should be tried most-specific
/// first.  A format with a distinctive header (e.g. a JSON envelope) should
/// precede a permissive one that matches almost anything.  Order is controlled
/// by the order of arguments in the constructor call in
/// <c>ServiceCollectionExtensions</c>.</para>
/// </summary>
public sealed class CompositeLogParser : ILogParser
{
    private readonly ILogParser[] _parsers;

    /// <param name="parsers">
    /// Parsers to try, in priority order.  The first one that returns
    /// <c>true</c> wins; remaining parsers are not called.
    /// </param>
    public CompositeLogParser(params ILogParser[] parsers)
    {
        _parsers = parsers;
    }

    /// <inheritdoc/>
    public bool TryParse(ReadOnlyMemory<byte> source, DateTimeOffset receivedAt, out LogEntry entry)
    {
        foreach (var parser in _parsers)
        {
            if (parser.TryParse(source, receivedAt, out entry))
                return true;
        }

        entry = default;
        return false;
    }
}
