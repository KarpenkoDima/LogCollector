using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using LogCollector.Application.Interfaces;
using LogCollector.Core.Domain;
using Microsoft.Extensions.Logging;

namespace LogCollector.Infrastructure.Sinks;

/// <summary>
/// Forwards log entries to Grafana Loki via the Push API.
///
/// <para><b>Loki Push API</b>: POST <c>{endpoint}/loki/api/v1/push</c></para>
/// <para>
/// Entries are grouped into streams by hostname so Loki can index efficiently.
/// Each stream carries the static labels configured in <c>appsettings.json</c>
/// plus a dynamic <c>hostname</c> label extracted from the entry.
/// </para>
///
/// <para><b>Timestamp format:</b>
/// Loki uses Unix nanoseconds as a string — <c>ReceivedAt</c> is converted
/// via <c>DateTimeOffset.ToUnixTimeMilliseconds() * 1_000_000</c>.</para>
/// </summary>
public sealed class LokiLogSink : ILogSink
{
    private readonly string _endpoint;
    private readonly IReadOnlyDictionary<string, string> _staticLabels;
    private readonly HttpClient _http;
    private readonly ILogger<LokiLogSink> _logger;

    public LokiLogSink(
        string endpoint,
        IReadOnlyDictionary<string, string> staticLabels,
        ILogger<LokiLogSink> logger)
    {
        _endpoint     = endpoint.TrimEnd('/');
        _staticLabels = staticLabels;
        _logger       = logger;

        // Single HttpClient for the lifetime of the sink.
        // In production, inject IHttpClientFactory to enable connection pooling
        // and named-client configuration (timeouts, retry handlers, etc.).
        _http = new HttpClient { BaseAddress = new Uri(_endpoint) };
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        // Validate connectivity on startup rather than at first write.
        // Loki's /ready endpoint returns 200 when ready to accept pushes.
        try
        {
            using var resp = await _http.GetAsync("/ready", ct);
            resp.EnsureSuccessStatusCode();
            _logger.LogInformation("[Loki] Connected — {Endpoint}", _endpoint);
        }
        catch (Exception ex)
        {
            // Log but do not throw — the service should still start; Loki might
            // become available shortly after boot.  Each SaveBatchAsync will retry
            // implicitly on the next batch.
            _logger.LogWarning(ex, "[Loki] Readiness check failed — {Endpoint}", _endpoint);
        }
    }

    public async Task SaveBatchAsync(IReadOnlyList<LogEntry> batch, CancellationToken ct)
    {
        // Group entries by hostname so each group becomes one Loki stream.
        // Within a stream, entries must be in ascending timestamp order.
        var streams = batch
            .GroupBy(e => Encoding.UTF8.GetString(e.Hostname.Span))
            .Select(g => BuildStream(g.Key, g.OrderBy(e => e.ReceivedAt)))
            .ToArray();

        var payload = new { streams };

        // Loki expects Content-Type: application/json
        using var response = await _http.PostAsJsonAsync(
            "/loki/api/v1/push", payload, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Loki push failed ({(int)response.StatusCode}): {body}");
        }

        _logger.LogDebug("[Loki] Pushed {Count} entries", batch.Count);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private object BuildStream(string hostname, IEnumerable<LogEntry> entries)
    {
        // Merge static labels with per-entry dynamic label
        var labels = _staticLabels
            .Append(KeyValuePair.Create("hostname", hostname))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        var values = entries.Select(e => new[]
        {
            // Loki timestamp: nanoseconds since epoch as a string
            (e.ReceivedAt.ToUnixTimeMilliseconds() * 1_000_000L)
                .ToString(CultureInfo.InvariantCulture),

            // Log line: "TOPIC,SEVERITY MESSAGE"
            $"{Encoding.UTF8.GetString(e.Topic.Span)},{e.Severity.ToString().ToLowerInvariant()} " +
            Encoding.UTF8.GetString(e.Message.Span),
        });

        return new { stream = labels, values };
    }
}
