using System.Net;
using System.Threading.Channels;
using LogCollector.Application.Interfaces;
using LogCollector.Application.Pipeline;
using LogCollector.Core.Domain;
using LogCollector.Infrastructure.Networking;
using LogCollector.Infrastructure.Observability;
using LogCollector.Infrastructure.Parsing;
using LogCollector.Infrastructure.Persistence;
using LogCollector.Infrastructure.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LogCollector.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLogCollector(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<BatchProcessorOptions>()
            .Bind(configuration.GetSection(BatchProcessorOptions.SectionName))
            .Validate(options => options.Capacity is >= 1 and <= 1_000_000,
                "Pipeline:Capacity must be between 1 and 1,000,000.")
            .Validate(options => options.BatchSize >= 1 && options.BatchSize <= options.Capacity,
                "Pipeline:BatchSize must be positive and not exceed Capacity.")
            .Validate(options => options.FlushInterval > TimeSpan.Zero,
                "Pipeline:FlushInterval must be positive.")
            .Validate(options => options.RetryDelay > TimeSpan.Zero,
                "Pipeline:RetryDelay must be positive.")
            .ValidateOnStart();

        services.AddOptions<UdpListenerOptions>()
            .Bind(configuration.GetSection(UdpListenerOptions.SectionName))
            .Validate(options => IPAddress.TryParse(options.Address, out _),
                "UdpListener:Address must be an IP address.")
            .Validate(options => options.Port is >= 1 and <= 65_535,
                "UdpListener:Port must be between 1 and 65535.")
            .Validate(options => options.MaxDatagramSize is >= 480 and <= 65_507,
                "UdpListener:MaxDatagramSize must be between 480 and 65507.")
            .Validate(options => options.SocketReceiveBufferSize >= options.MaxDatagramSize,
                "UdpListener:SocketReceiveBufferSize must not be smaller than MaxDatagramSize.")
            .ValidateOnStart();

        services.AddOptions<SqliteOptions>()
            .Bind(configuration.GetSection(SqliteOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.ConnectionString),
                "Sqlite:ConnectionString is required.")
            .Validate(options => options.BusyTimeoutSeconds is >= 1 and <= 300,
                "Sqlite:BusyTimeoutSeconds must be between 1 and 300.")
            .ValidateOnStart();

        services.AddOptions<LokiOptions>()
            .Bind(configuration.GetSection(LokiOptions.SectionName))
            .Validate(options => !options.Enabled ||
                (Uri.TryCreate(options.Endpoint, UriKind.Absolute, out Uri? endpoint) &&
                 endpoint.Scheme is "http" or "https"),
                "Loki:Endpoint must be an absolute HTTP or HTTPS URL.")
            .Validate(options => options.Timeout > TimeSpan.Zero,
                "Loki:Timeout must be positive.")
            .Validate(options => options.Labels.All(label =>
                    IsValidLokiLabelName(label.Key) &&
                    label.Key is not "hostname" and not "topic" and not "severity"),
                "Loki label names must be valid and must not replace hostname, topic, or severity.")
            .ValidateOnStart();

        services.AddSingleton<ILogParser, Rfc3164Parser>();
        bool lokiEnabled = configuration.GetValue<bool>($"{LokiOptions.SectionName}:Enabled");
        if (lokiEnabled)
        {
            services.AddHttpClient<LokiLogPublisher>((provider, client) =>
            {
                LokiOptions options = provider.GetRequiredService<IOptions<LokiOptions>>().Value;
                client.BaseAddress = new Uri(options.Endpoint.TrimEnd('/') + "/", UriKind.Absolute);
                client.Timeout = options.Timeout;
            });
            services.AddSingleton<ILogObserver>(provider =>
                provider.GetRequiredService<LokiLogPublisher>());
        }

        services.AddSingleton<ILogRepository>(provider =>
        {
            // ActivatorUtilities creates the primary without registering it as a second
            // disposable singleton; ObservedLogRepository is its sole owner.
            var primary = ActivatorUtilities.CreateInstance<SqliteLogRepository>(provider);
            return new ObservedLogRepository(
                primary,
                provider.GetServices<ILogObserver>(),
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ObservedLogRepository>>());
        });

        services.AddSingleton(provider =>
        {
            BatchProcessorOptions options = provider
                .GetRequiredService<IOptions<BatchProcessorOptions>>().Value;
            return Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(options.Capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });
        });
        services.AddSingleton(provider => provider.GetRequiredService<Channel<LogEntry>>().Reader);
        services.AddSingleton(provider => provider.GetRequiredService<Channel<LogEntry>>().Writer);
        services.AddSingleton(provider => new BatchProcessor(
            provider.GetRequiredService<ILogRepository>(),
            provider.GetRequiredService<IOptions<BatchProcessorOptions>>().Value));

        // Registration order is deliberate: the listener stops first and completes
        // the channel; the writer then drains it before the host exits.
        services.AddHostedService<BatchWriterService>();
        services.AddHostedService<UdpSyslogListener>();
        return services;
    }

    private static bool IsValidLokiLabelName(string name)
    {
        if (name.Length == 0 || !(name[0] == '_' || char.IsAsciiLetter(name[0])))
            return false;

        for (int i = 1; i < name.Length; i++)
        {
            if (name[i] != '_' && !char.IsAsciiLetterOrDigit(name[i]))
                return false;
        }

        return true;
    }
}
