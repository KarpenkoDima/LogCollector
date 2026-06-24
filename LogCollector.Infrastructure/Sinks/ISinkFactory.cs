using LogCollector.Application.Interfaces;
using Microsoft.Extensions.Configuration;

namespace LogCollector.Infrastructure.Sinks;

/// <summary>
/// Creates an <see cref="ILogSink"/> from a configuration section.
///
/// <para>
/// Each sink type registers one factory.  <c>ServiceCollectionExtensions</c>
/// reads the <c>"LogSinks"</c> array from <c>appsettings.json</c>, finds the
/// matching factory by <see cref="SinkType"/>, and calls <see cref="Create"/>
/// to produce the sink instance.
/// </para>
///
/// <para><b>Adding a new destination (e.g. ClickHouse):</b></para>
/// <list type="number">
///   <item>Create <c>ClickHouseLogSink : ILogSink</c>.</item>
///   <item>Create <c>ClickHouseSinkFactory : ISinkFactory</c> with <see cref="SinkType"/> = <c>"ClickHouse"</c>.</item>
///   <item>Register: <c>services.AddSingleton&lt;ISinkFactory, ClickHouseSinkFactory&gt;()</c>.</item>
///   <item>Add to <c>appsettings.json</c>: <c>{{ "Type": "ClickHouse", "ConnectionString": "..." }}</c>.</item>
/// </list>
/// No other files change.
/// </summary>
public interface ISinkFactory
{
    /// <summary>
    /// Matches the <c>"Type"</c> field in a <c>LogSinks</c> config entry.
    /// Comparison is case-insensitive.
    /// </summary>
    string SinkType { get; }

    /// <summary>
    /// Constructs the sink from the sink-specific config section.
    /// The section contains all keys below the <c>"Type"</c> field
    /// (e.g. <c>"ConnectionString"</c>, <c>"Endpoint"</c>, <c>"Labels"</c>).
    /// </summary>
    ILogSink Create(IConfigurationSection config, IServiceProvider services);
}
