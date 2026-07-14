using System.Net;
using System.Text.Json;
using LogCollector.Core.Domain;
using LogCollector.Infrastructure.Observability;
using Microsoft.Extensions.Options;
using Xunit;

namespace LogCollector.Tests.Observability;

public sealed class LokiLogPublisherTests
{
    [Fact]
    public async Task SendsGroupedStreamsWithStringNanosecondTimestamps()
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://loki:3100/") };
        var publisher = new LokiLogPublisher(client, Options.Create(new LokiOptions
        {
            Enabled = true,
            Labels = new Dictionary<string, string> { ["app"] = "test" },
        }));

        await publisher.PublishBatchAsync(
            [Entry("router-1", "firewall", "accepted"), Entry("router-1", "firewall", "dropped")],
            CancellationToken.None);

        Assert.Equal(new Uri("http://loki:3100/loki/api/v1/push"), handler.RequestUri);
        using JsonDocument document = JsonDocument.Parse(handler.Body);
        JsonElement stream = document.RootElement.GetProperty("streams")[0];
        Assert.Equal("test", stream.GetProperty("stream").GetProperty("app").GetString());
        Assert.Equal("router-1", stream.GetProperty("stream").GetProperty("hostname").GetString());
        Assert.Equal(2, stream.GetProperty("values").GetArrayLength());
        Assert.Equal(JsonValueKind.String, stream.GetProperty("values")[0][0].ValueKind);
        Assert.Equal("accepted", stream.GetProperty("values")[0][1].GetString());
    }

    private static LogEntry Entry(string hostname, string topic, string message) => new()
    {
        Priority = 132,
        Timestamp = "Jul 14 12:30:45"u8.ToArray(),
        Hostname = System.Text.Encoding.UTF8.GetBytes(hostname),
        Topic = System.Text.Encoding.UTF8.GetBytes(topic),
        Message = System.Text.Encoding.UTF8.GetBytes(message),
        ReceivedAt = new DateTimeOffset(2026, 7, 14, 12, 30, 45, TimeSpan.Zero),
    };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public byte[] Body { get; private set; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
