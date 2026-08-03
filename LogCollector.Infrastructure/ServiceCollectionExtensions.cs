using System.Threading.Channels;
using LogCollector.Application.Interfaces;
using LogCollector.Core.Domain;
using LogCollector.Infrastructure.Listeners;
using LogCollector.Infrastructure.Parsers;
using LogCollector.Infrastructure.Pipeline;
using LogCollector.Infrastructure.Sinks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LogCollector.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all Infrastructure services.
    ///
    /// <para><b>Adding a new log destination (e.g. ClickHouse):</b></para>
    /// <list type="number">
    ///   <item>Write <c>ClickHouseLogSink : ILogSink</c>.</item>
    ///   <item>Write <c>ClickHouseSinkFactory : ISinkFactory</c> with <c>SinkType = "ClickHouse"</c>.</item>
    ///   <item>Add <c>services.AddSingleton&lt;ISinkFactory, ClickHouseSinkFactory&gt;()</c> below.</item>
    ///   <item>Add <c>{{ "Type": "ClickHouse", ... }}</c> to <c>appsettings.json → LogSinks</c>.</item>
    /// </list>
    /// Nothing else changes.
    /// </summary>
    public static IServiceCollection AddLogCollectorInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ── Options ───────────────────────────────────────────────────────────
 
        // ── Options with startup validation (P1.3) ────────────────────────────
        // AddOptions + Validate + ValidateOnStart: a bad value crashes the host
        // at boot with a clear message, before any traffic is accepted, instead
        // of failing silently or much later on first use.
        services.AddOptions<BatchWriterOptions>()
            .Bind(configuration.GetSection("BatchWriter"))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<BatchWriterOptions>,
            BatchWriterOptionsValidator>();

        services.AddOptions<SyslogListenerOptions>()
            .Bind(configuration.GetSection("SyslogListener"))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SyslogListenerOptions>,
            SyslogListenerOptionsValidator>();

        // ── Sink factories ────────────────────────────────────────────────────
        // Register one ISinkFactory per supported destination type.
        // The type string here must match the "Type" key in appsettings.json → LogSinks[].
        services.AddSingleton<ISinkFactory, SqliteSinkFactory>();
        services.AddSingleton<ISinkFactory, LokiSinkFactory>();
        services.AddSingleton<ISinkFactory, ConsoleSinkFactory>();
        // services.AddSingleton<ISinkFactory, ClickHouseSinkFactory>(); ← add here

        // ── Repository: config-driven fanout ─────────────────────────────────
        //
        // Reads the "LogSinks" array from appsettings.json.
        // For each entry, finds the ISinkFactory whose SinkType matches "Type",
        // calls Create(), and wraps the resulting sinks in FanOutLogRepository.
        //
        // Example appsettings.json:
        //   "LogSinks": [
        //     { "Type": "Sqlite", "ConnectionString": "Data Source=logs.db" },
        //     { "Type": "Loki",   "Endpoint": "http://loki:3100" }
        //   ]
        services.AddSingleton<ILogRepository>(sp =>
        {
            var factories = sp.GetServices<ISinkFactory>()
                .ToDictionary(f => f.SinkType, StringComparer.OrdinalIgnoreCase);

            var sinksConfig = configuration
                .GetSection("LogSinks")
                .GetChildren()
                .ToArray();

            if (sinksConfig.Length == 0)
                throw new InvalidOperationException(
                    "No log sinks configured. " +
                    "Add at least one entry to 'LogSinks' in appsettings.json.\n" +
                    $"Available types: {string.Join(", ", factories.Keys)}");

            // Build (type, sink) pairs so we can split primary from secondaries.
            var built = sinksConfig.Select(section =>
            {
                var type = section["Type"]
                    ?? throw new InvalidOperationException(
                        "A 'LogSinks' entry is missing the required 'Type' field.");

                if (!factories.TryGetValue(type, out var factory))
                    throw new InvalidOperationException(
                        $"No factory registered for sink type '{type}'. " +
                        $"Available: {string.Join(", ", factories.Keys)}");

                return (Type: type, Sink: factory.Create(section, sp));
            }).ToArray();

            // ── P1.1: SQLite is the mandatory PRIMARY sink ────────────────────
            // Primary is awaited first and its failure fails the batch (real loss).
            // Loki/Console are best-effort secondaries whose failure or hang never
            // fails the batch. SQLite must be configured — it is the source of truth.
            var primaryPair = built.FirstOrDefault(
                b => string.Equals(b.Type, "Sqlite", StringComparison.OrdinalIgnoreCase));

            // P1.3: ровно один SQLite primary — ноль или несколько это ошибка конфига.
            var sqliteCount = built.Count(
                b => string.Equals(b.Type, "Sqlite", StringComparison.OrdinalIgnoreCase));

            if (sqliteCount == 0)
                throw new InvalidOperationException(
                    "A 'Sqlite' sink is required as the primary destination. " +
                    "Add { \"Type\": \"Sqlite\", ... } to 'LogSinks'. " +
                    "Loki/Console are best-effort secondaries and cannot be primary.");

            if (sqliteCount > 1)
                throw new InvalidOperationException(
                    $"Exactly one 'Sqlite' primary sink is allowed, found {sqliteCount}. " +
                    "Remove the extra Sqlite entries from 'LogSinks'.");

            var secondaries = built
                .Where(b => !ReferenceEquals(b.Sink, primaryPair.Sink))
                .Select(b => b.Sink)
                .ToArray();

            // Secondary timeout: how long a hung Loki may hold the pipeline before
            // being skipped. Batch is already persisted by SQLite at this point.
            var secondaryTimeout = TimeSpan.FromSeconds(5);

            return new FanOutLogRepository(
                primaryPair.Sink,
                secondaries,
                secondaryTimeout,
                sp.GetRequiredService<ILogger<FanOutLogRepository>>());
        });

        // ── Parsers ───────────────────────────────────────────────────────────
        services.AddSingleton<MikroTikSyslogParser>();
        // services.AddSingleton<WinBeatLogParser>(); ← add when ready

        services.AddSingleton<ILogParser>(sp => new CompositeLogParser(
            sp.GetRequiredService<MikroTikSyslogParser>()
            // sp.GetRequiredService<WinBeatLogParser>() ← and here
        ));

        // ── Owned ingress (P0.1 fix) ──────────────────────────────────────────
        // Раньше здесь был голый Channel с FullMode.DropOldest. Дефект: канал
        // вытеснял LogEntry, НЕ вызывая RawBuffer.Dispose() — утечка pool-буферов
        // под нагрузкой. OwnedIngress реализует drop-oldest ЯВНО, с Dispose
        // вытесняемого буфера ровно один раз. Политика «freshest wins» сохранена.
        services.AddSingleton<IOwnedIngress>(_ => new OwnedIngress(capacity: 10_000));

        // Reader для BatchWriterService — берём из ingress, сигнатура сервиса
        // не меняется (он по-прежнему принимает ChannelReader<LogEntry>).
        services.AddSingleton(sp =>
            sp.GetRequiredService<IOwnedIngress>().Reader);

        // ── Hosted Services ───────────────────────────────────────────────────
        services.AddHostedService<BatchWriterService>();  // stopped last  — drains
        services.AddHostedService<UdpSyslogListener>();   // stopped first — stops producing

        return services;
    }
}
