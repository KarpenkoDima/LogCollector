namespace LogCollector.Infrastructure.Observability;

public sealed class LokiOptions
{
    public const string SectionName = "Loki";

    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = "http://loki:3100";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);
    public Dictionary<string, string> Labels { get; set; } = new(StringComparer.Ordinal)
    {
        ["app"] = "logcollector",
    };
}
