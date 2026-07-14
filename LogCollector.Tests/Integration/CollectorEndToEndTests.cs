using System.Net;
using System.Net.Sockets;
using Dapper;
using LogCollector.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LogCollector.Tests.Integration;

public sealed class CollectorEndToEndTests
{
    [Fact]
    public async Task ReceivesUdpDatagramAndWritesItToSqlite()
    {
        int port = GetUnusedUdpPort();
        string path = Path.Combine(Path.GetTempPath(), $"logcollector-e2e-{Guid.NewGuid():N}.db");
        string connectionString = $"Data Source={path};Cache=Shared";
        var settings = new Dictionary<string, string?>
        {
            ["UdpListener:Address"] = "127.0.0.1",
            ["UdpListener:Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["UdpListener:MaxDatagramSize"] = "8192",
            ["UdpListener:SocketReceiveBufferSize"] = "65536",
            ["Pipeline:Capacity"] = "32",
            ["Pipeline:BatchSize"] = "8",
            ["Pipeline:FlushInterval"] = "00:00:00.050",
            ["Pipeline:RetryDelay"] = "00:00:00.010",
            ["Sqlite:ConnectionString"] = connectionString,
            ["Sqlite:BusyTimeoutSeconds"] = "2",
        };

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddLogCollector(builder.Configuration);
        IHost host = builder.Build();

        try
        {
            await host.StartAsync();
            await Task.Delay(50);

            using var client = new UdpClient(AddressFamily.InterNetwork);
            byte[] datagram = "<132>Jul 14 12:30:45 edge : firewall,info accepted"u8.ToArray();
            await client.SendAsync(datagram, new IPEndPoint(IPAddress.Loopback, port));

            StoredLog? stored = await PollForLogAsync(connectionString, TimeSpan.FromSeconds(5));
            Assert.NotNull(stored);
            Assert.Equal("edge", stored.Hostname);
            Assert.Equal("firewall", stored.Topic);
            Assert.Equal("accepted", stored.Message);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
            SqliteConnection.ClearAllPools();
            File.Delete(path);
            File.Delete(path + "-shm");
            File.Delete(path + "-wal");
        }
    }

    private static async Task<StoredLog?> PollForLogAsync(
        string connectionString,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                await using var connection = new SqliteConnection(connectionString);
                StoredLog? row = await connection.QuerySingleOrDefaultAsync<StoredLog>(
                    "SELECT hostname, topic, message FROM logs LIMIT 1;");
                if (row is not null)
                    return row;
            }
            catch (SqliteException)
            {
                // The background repository may still be creating the database/schema.
            }

            await Task.Delay(25);
        }

        return null;
    }

    private static int GetUnusedUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private sealed class StoredLog
    {
        public string Hostname { get; init; } = string.Empty;
        public string? Topic { get; init; }
        public string Message { get; init; } = string.Empty;
    }
}
