using LogCollector.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LogCollector.Infrastructure.Sinks;

/// <summary>
/// Creates a <see cref="SqliteLogSink"/> from a config section.
/// </summary>
/// <example>
/// appsettings.json:
/// <code>
/// "LogSinks": [
///   {
///     "Type": "Sqlite",
///     "ConnectionString": "Data Source=/var/log/logcollector/logs.db"
///   }
/// ]
/// </code>
/// </example>
public sealed class SqliteSinkFactory : ISinkFactory
{
    public string SinkType => "Sqlite";

    public ILogSink Create(IConfigurationSection config, IServiceProvider services)
    {
        var cs = config["ConnectionString"]
            ?? throw new InvalidOperationException(
                "Sqlite sink requires a 'ConnectionString' key in its config section.");

        return new SqliteLogSink(
            cs,
            services.GetRequiredService<ILogger<SqliteLogSink>>());
    }
}
