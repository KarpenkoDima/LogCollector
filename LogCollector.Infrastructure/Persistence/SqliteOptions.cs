namespace LogCollector.Infrastructure.Persistence;

public sealed class SqliteOptions
{
    public const string SectionName = "Sqlite";

    public string ConnectionString { get; set; } = "Data Source=logs.db";
    public int BusyTimeoutSeconds { get; set; } = 5;
}
