using LogCollector.Infrastructure;
using Microsoft.Extensions.Hosting;

// ── Composition Root ──────────────────────────────────────────────────────────
//
// Program.cs is the only place in the entire solution that sees every layer at
// once.  Its job is purely assembly: take the pieces built in Infrastructure
// and Core, wire them together via DI, and hand control to the Generic Host.
// No business logic belongs here.
//
// Host.CreateApplicationBuilder (introduced in .NET 7) sets up the following
// automatically — no extra code required:
//
//   Configuration sources, in ascending priority order:
//     1. appsettings.json               ← production defaults
//     2. appsettings.{ENVIRONMENT}.json ← per-environment overrides
//     3. Environment variables          ← ops-team overrides without file edits
//     4. Command-line arguments         ← one-off overrides at launch
//
//   Environment variable naming convention for nested keys:
//     "SyslogListener:Port" → SYSLOGLISTENER__PORT=514
//   (double underscore is the section separator in env vars)
//
//   Logging: Console provider + ILogger<T> registered by default.
//   Configure levels per namespace in appsettings.json → "Logging" section.

var builder = Host.CreateApplicationBuilder(args);

// ── Cross-platform daemon integration ─────────────────────────────────────────
//
// AddSystemd() is safe to call everywhere — it activates only when the process
// is started by systemd (detected by the presence of $INVOCATION_ID).
//
// When active, it does three things:
//   1. Sends sd_notify("READY=1") once all IHostedService.StartAsync calls
//      complete, allowing "Type=notify" in the .service file.  Without this,
//      systemd would consider the service "started" before the UDP socket is
//      bound, causing log-loss during boot.
//
//   2. Translates SIGTERM → IApplicationLifetime.StoppingToken, triggering our
//      graceful drain phase in BatchWriterService before the process exits.
//
//   3. Switches the console logger to the journald-compatible format, so
//      "journalctl -u logcollector -f" shows structured log output.
//
// On dev machines, Docker, and Windows this call is a complete no-op.
builder.Services.AddSystemd();

// ── Infrastructure wiring ─────────────────────────────────────────────────────
//
// One extension method registers everything:
//   - Channel<LogEntry> (bounded, capacity 10 000, BoundedChannelFullMode.Wait)
//   - UdpSyslogListener  (BackgroundService, reads from network)
//   - BatchWriterService (BackgroundService, writes to SQLite)
//   - IOptions<SyslogListenerOptions> bound to "SyslogListener" config section
//   - IOptions<BatchWriterOptions>    bound to "BatchWriter"    config section
//
// Registration order inside AddLogCollectorInfrastructure is intentional:
// BatchWriterService registers first → stops last (drains the channel).
// UdpSyslogListener registers second → stops first (stops producing).
builder.Services.AddLogCollectorInfrastructure(builder.Configuration);

// ── Run ────────────────────────────────────────────────────────────────────────
//
// Build() finalises the DI container (validates registrations, builds providers).
// RunAsync() starts all IHostedService instances in registration order, then
// blocks until a shutdown signal is received (SIGTERM on Linux, Ctrl+C anywhere).
//
// On shutdown, the host calls IHostedService.StopAsync in reverse order:
//   1. UdpSyslogListener.StopAsync  → socket closed, no new datagrams
//   2. BatchWriterService.StopAsync → drains remaining channel entries to SQLite
//
// The process only exits after both StopAsync calls complete or
// ShutdownTimeout (default 5 s) elapses, whichever comes first.
await builder.Build().RunAsync();
