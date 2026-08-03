using Microsoft.Extensions.Options;

namespace LogCollector.Infrastructure.Listeners;

/// <summary>
/// Validates <see cref="SyslogListenerOptions"/> at startup (P1.3).
///
/// Registered with <c>ValidateOnStart()</c> so a bad value crashes the host
/// during boot — before the listener binds a socket and accepts traffic —
/// with a clear message, instead of failing silently or much later.
/// </summary>
public sealed class SyslogListenerOptionsValidator : IValidateOptions<SyslogListenerOptions>
{
    public ValidateOptionsResult Validate(string? name, SyslogListenerOptions options)
    {
        var errors = new List<string>();

        // Port: valid TCP/UDP port range.
        if (options.Port is < 1 or > 65_535)
            errors.Add($"Port must be in [1, 65535], got {options.Port}.");

        // MaxDatagramSize: min 256 (RFC-ish floor), max 65507 (UDP payload limit).
        if (options.MaxDatagramSize is < 256 or > 65_507)
            errors.Add(
                $"MaxDatagramSize must be in [256, 65507], got {options.MaxDatagramSize}.");

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
