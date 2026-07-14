using System.Buffers;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LogCollector.Application.Interfaces;
using LogCollector.Core.Domain;
using Microsoft.Extensions.Options;

namespace LogCollector.Infrastructure.Observability;

/// <summary>Pushes batches to Loki's JSON HTTP API, grouped by bounded labels.</summary>
public sealed class LokiLogPublisher : ILogObserver
{
    private static readonly long UnixEpochTicks = DateTimeOffset.UnixEpoch.Ticks;
    private readonly HttpClient _httpClient;
    private readonly LokiOptions _options;

    public LokiLogPublisher(HttpClient httpClient, IOptions<LokiOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public async Task PublishBatchAsync(
        IReadOnlyList<LogEntry> entries,
        CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
            return;

        Dictionary<StreamKey, List<LogEntry>> streams = GroupByStream(entries);
        var buffer = new ArrayBufferWriter<byte>(Math.Max(1_024, entries.Count * 256));
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteStartArray("streams");

            foreach ((StreamKey key, List<LogEntry> values) in streams)
            {
                json.WriteStartObject();
                json.WriteStartObject("stream");
                foreach ((string label, string value) in _options.Labels)
                    json.WriteString(label, value);
                json.WriteString("hostname", key.Hostname);
                json.WriteString("topic", key.Topic);
                json.WriteString("severity", key.Severity);
                json.WriteEndObject();

                json.WriteStartArray("values");
                foreach (LogEntry entry in values)
                {
                    json.WriteStartArray();
                    json.WriteStringValue(ToNanoseconds(entry.ReceivedAt));
                    json.WriteStringValue(Encoding.UTF8.GetString(entry.Message.Span));
                    json.WriteEndArray();
                }
                json.WriteEndArray();
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "loki/api/v1/push")
        {
            Content = new ReadOnlyMemoryContent(buffer.WrittenMemory),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using HttpResponseMessage response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private static Dictionary<StreamKey, List<LogEntry>> GroupByStream(
        IReadOnlyList<LogEntry> entries)
    {
        var result = new Dictionary<StreamKey, List<LogEntry>>();
        foreach (LogEntry entry in entries)
        {
            var key = new StreamKey(
                Encoding.UTF8.GetString(entry.Hostname.Span),
                entry.Topic.IsEmpty ? "unknown" : Encoding.UTF8.GetString(entry.Topic.Span),
                entry.Severity.ToString().ToLowerInvariant());

            if (!result.TryGetValue(key, out List<LogEntry>? stream))
            {
                stream = [];
                result.Add(key, stream);
            }

            stream.Add(entry);
        }

        foreach (List<LogEntry> stream in result.Values)
            stream.Sort(static (left, right) => left.ReceivedAt.CompareTo(right.ReceivedAt));

        return result;
    }

    private static string ToNanoseconds(DateTimeOffset value)
    {
        long nanoseconds = checked((value.UtcTicks - UnixEpochTicks) * 100L);
        return nanoseconds.ToString(CultureInfo.InvariantCulture);
    }

    private readonly record struct StreamKey(string Hostname, string Topic, string Severity);
}
