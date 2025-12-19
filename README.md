# LogCollector

A high-performance syslog ingestion service for .NET 9 that collects RFC 3164 messages from MikroTik routers and Windows machines and batch-writes them to a local SQLite database. The primary design constraint is zero heap allocation on the hot receive path at sustained rates of 10 000 entries per second.

---

## How data flows through the system

```
MikroTik Router
  │
  │  UDP datagram  (<30>Jun  4 18:00:00 mtk-router : firewall,info forward: ...)
  ▼
UdpSyslogListener          ← BackgroundService, bound to UDP port 514
  │
  │  pinned receive buffer  (GC.AllocateArray, fixed in Pinned Object Heap)
  │  ↓ one CopyTo per datagram
  │  pool-rented buffer     (MemoryPool<byte>.Shared)
  │
  │  SyslogParser.TryParse  (zero allocation — IndexOf + span slices only)
  │
  │  LogEntry               (readonly struct, ~96 bytes on the stack)
  ▼
Channel<LogEntry>          ← BoundedChannel, capacity 10 000, FullMode.Wait
  │
  │  WaitToReadAsync  →  TryRead × N  (Cleary's batch-drain pattern)
  ▼
BatchWriterService         ← BackgroundService, one SQLite transaction per batch
  │
  │  Encoding.UTF8.GetString  ← strings allocated HERE, nowhere else
  ▼
SQLite (WAL mode)
```

The two `BackgroundService` instances run on separate threads. The channel decouples their speeds: the listener can receive at network rate while the writer processes at its own pace. When the channel fills to capacity, `WriteAsync` suspends the listener — this is intentional backpressure that prevents unbounded memory growth during traffic spikes.

---

## Solution structure

```
LogCollector/
├── LogCollector.Core/
│   └── Domain/
│       ├── LogEntry.cs          readonly struct — the unit of work through the pipeline
│       └── SyslogSeverity.cs    enum (byte-backed) — debug / info / warning / error / critical
│
├── LogCollector.Infrastructure/
│   ├── Parsers/
│   │   └── SyslogParser.cs      zero-allocation RFC 3164 + MikroTik extension parser
│   ├── Listeners/
│   │   ├── UdpSyslogListener.cs BackgroundService — binds UDP socket, feeds Channel
│   │   └── SyslogListenerOptions.cs
│   ├── Pipeline/
│   │   ├── BatchWriterService.cs BackgroundService — drains Channel, writes SQLite
│   │   └── BatchWriterOptions.cs
│   └── ServiceCollectionExtensions.cs   one-call DI registration for the whole layer
│
├── LogCollector.Host/
│   ├── Program.cs               composition root — three lines of code
│   ├── appsettings.json         production defaults
│   ├── appsettings.Development.json
│   └── logcollector.service     systemd unit file
│
└── LogCollector.Tests/
    ├── Parsers/
    │   └── SyslogParserTests.cs  unit tests + zero-copy architectural proof
    └── Pipeline/
        └── BatchWriterServiceTests.cs  integration tests against real in-memory SQLite
```

The dependency arrows point inward only: Host knows about Infrastructure and Core; Infrastructure knows about Core; Core knows nothing about the other layers. This is Clean Architecture's dependency rule enforced at the compiler level — if Infrastructure ever tried to reference Host, the build would fail.

---

## Getting started

**Prerequisites:** .NET 9 SDK and a terminal.

```bash
# Clone and build
git clone <repo-url>
cd LogCollector
dotnet build

# Run all tests
dotnet test

# Start in development mode (listens on port 5140, writes to dev-logs.db)
cd LogCollector.Host
dotnet run
```

Once the service is running, send a test datagram from a second terminal:

```bash
echo "<30>Jun  4 18:00:00 mtk-router : firewall,info forward: in:ether1 out:bridge, proto TCP, 192.168.88.100:55000 -> 10.0.0.5:80" \
  | nc -u -w1 localhost 5140
```

The entry should appear in `dev-logs.db` within one second (the development `BatchTimeout`). You can inspect it with any SQLite client:

```bash
sqlite3 dev-logs.db "SELECT Hostname, Severity, Message FROM Logs ORDER BY Id DESC LIMIT 10;"
```

---

## MikroTik configuration

Point your router at the collector. Log in to the MikroTik CLI and run:

```
/system logging action
set remote remote-address=<COLLECTOR_IP> remote-port=5140 src-address=0.0.0.0

/system logging
add action=remote topics=firewall
add action=remote topics=dhcp
add action=remote topics=system
```

For production (port 514), change `remote-port=514`. Replace `5140` with whatever `SyslogListener.Port` is set to in your configuration.

---

## Configuration reference

All settings live in `appsettings.json` and can be overridden per-environment or via environment variables. The environment variable naming convention uses double underscore as the section separator: `"SyslogListener": { "Port": 514 }` becomes `SYSLOGLISTENER__PORT=514`.

**`SyslogListener` section**

`Port` (default `514`) is the UDP port to bind. Ports below 1024 require a capability on Linux — see the deployment section. `MaxDatagramSize` (default `8192`) controls the size of the single pinned receive buffer that lives for the entire service lifetime; it should be larger than the longest datagram you expect to receive.

**`BatchWriter` section**

`BatchSize` (default `500`) is the maximum number of entries written in a single SQLite transaction. Larger values improve throughput but increase per-batch latency. `BatchTimeout` (default `"00:00:02"`, format `HH:MM:SS`) is the maximum time to wait before flushing a partial batch — this keeps entries from stalling in the channel during low-traffic periods. `ConnectionString` (default `"Data Source=/var/log/logcollector/logs.db"`) is a standard SQLite connection string; the WAL journal mode and appropriate pragmas are set automatically at startup.

---

## Production deployment

**Build a self-contained binary:**

```bash
dotnet publish LogCollector.Host -c Release -r linux-x64 --self-contained true -o /opt/logcollector
```

**Create the service account and log directory:**

```bash
sudo useradd -r -s /sbin/nologin logcollector
sudo mkdir -p /var/log/logcollector
sudo chown logcollector:logcollector /var/log/logcollector
```

**Grant the binary permission to bind port 514 without running as root:**

```bash
sudo setcap cap_net_bind_service+ep /opt/logcollector/LogCollector.Host
```

This grants a single, specific Linux capability to the file. The process still runs as the unprivileged `logcollector` user — `setcap` only allows it to bind privileged ports. The systemd unit file (`logcollector.service`) reinforces this with `AmbientCapabilities=CAP_NET_BIND_SERVICE` so the capability is inherited correctly.

If you prefer not to use capabilities, set `Port` to `5140` and add a kernel-level redirect instead:

```bash
sudo iptables -t nat -A PREROUTING -p udp --dport 514 -j REDIRECT --to-port 5140
```

**Install and start the systemd service:**

```bash
sudo cp /opt/logcollector/logcollector.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now logcollector
sudo systemctl status logcollector
```

**Follow logs:**

```bash
journalctl -u logcollector -f
```

Because `Program.cs` calls `builder.Services.AddSystemd()`, the service sends `sd_notify("READY=1")` once the UDP socket is bound and the SQLite schema is verified. The unit file uses `Type=notify`, so `systemctl start` blocks until that signal arrives — you will never see the service reported as "active" before it is genuinely ready to receive datagrams.

---

## Design decisions

Three architectural rules shaped every file in this codebase. They are worth understanding because they explain choices that might otherwise look unusual.

### Zero allocation on the hot path (Kokosa's rule)

At 10 000 datagrams per second, even a single `new byte[]` inside the receive loop produces 10 000 Gen0 objects per second. The .NET GC handles Gen0 very efficiently, but at that rate it adds measurable "stop the world" pauses every few hundred milliseconds — unacceptable for a latency-sensitive collector.

The solution is a two-buffer design. A single `byte[]` allocated with `GC.AllocateArray<byte>(size, pinned: true)` lives in the Pinned Object Heap for the entire service lifetime and receives every datagram. After each receive, the datagram bytes are copied into a segment rented from `MemoryPool<byte>.Shared` (backed by `ArrayPool<byte>`) — this is the one allocation per datagram that cannot be avoided, because `LogEntry` needs a stable memory region to point at while it travels through the channel.

`SyslogParser` then operates entirely on `ReadOnlySpan<byte>` using `IndexOf` and range slices. No `string.Split`, no `Substring`, no intermediate strings. The zero-copy guarantee is formally verified by a unit test that uses `MemoryMarshal.TryGetArray` to confirm that every `ReadOnlyMemory<byte>` field in the parsed `LogEntry` shares the exact same backing array — and the exact same byte offset — as the original source buffer.

Strings are created in exactly one place: `BatchWriterService.WriteBatchAsync`, in the call to `Encoding.UTF8.GetString(span)` immediately before each `SqliteParameter` is assigned. They live briefly, get serialized into the SQLite wire protocol, and become Gen0 garbage. Because they are short-lived and collected in bulk during the next Gen0 sweep, their allocation cost is negligible.

### Bounded channel with backpressure (Cleary's rule)

The channel between the listener and the writer is deliberately bounded at 10 000 entries — roughly one second of traffic at peak rate. When it fills, `ChannelWriter.WriteAsync` suspends the listener rather than dropping datagrams or growing unboundedly.

This is backpressure, not data loss. While the listener is suspended, the OS-level socket receive buffer absorbs incoming datagrams. The router eventually notices they are not being acknowledged and slows down. The system as a whole degrades gracefully under load rather than consuming unbounded memory and crashing.

The batch-drain pattern — `WaitToReadAsync` followed by `TryRead` in a tight loop — is the idiomatic way to consume a channel in bulk. `WaitToReadAsync` parks the consumer thread cheaply while the channel is empty. Once data arrives, `TryRead` pulls entries synchronously without returning to the scheduler, which is what produces actual batching behaviour. A 500-entry batch means one SQLite `COMMIT` instead of 500, which on a typical SSD is the difference between 500 ms/s of `fsync` time and 1 ms/s.

### `readonly struct` for `LogEntry` (Richter's rule)

`LogEntry` is a value type. When the listener calls `await _writer.WriteAsync(entry, ct)`, the struct is copied into the channel's internal ring-buffer array. When the writer calls `_reader.TryRead(out var entry)`, it is copied back out. Both copies are stack-to-array or array-to-stack `memcpy` operations — fast, predictable, and invisible to the GC.

The alternative — a `record class` or any reference type — would allocate a new heap object for every log entry. 10 000 heap objects per second all in Gen0, all collected every few hundred milliseconds. The `readonly struct` makes the GC irrelevant to the hot path entirely.

The struct carries one managed reference: `IMemoryOwner<byte>? RawBuffer`. This is the ownership handle for the pool-rented buffer. The GC does track this reference, but it tracks one `IMemoryOwner<byte>` per entry rather than one `LogEntry` object per entry — the tracking cost is the same, but the allocation cost of the `LogEntry` itself disappears.

---

## Testing

```bash
dotnet test --logger "console;verbosity=detailed"
```

The test suite contains 19 tests across two classes.

`SyslogParserTests` covers every field extraction, all five severity levels, RFC 3164 edge cases (single-digit day padding, trailing CR/LF), and malformed input. The most structurally interesting test is `TryParse_AllMemoryFields_AreSlicesOfSourceBuffer_NoBytesAreCopied`, which uses `MemoryMarshal.TryGetArray` to open up the parsed `LogEntry` and verify — at the byte level — that each field points into the original source array with the correct offset and length.

`BatchWriterServiceTests` runs against a real SQLite database using the named shared-memory mode (`Mode=Memory;Cache=Shared`). A "guardian connection" kept open for the duration of each test prevents SQLite from discarding the in-memory database between the batch writer's per-batch connections. The tests cover the timer flush path (sparse traffic), the batch-full path (burst traffic), graceful shutdown drain, and — critically — that `RawBuffer.Dispose()` is called regardless of whether the SQLite write succeeded or threw an exception.
