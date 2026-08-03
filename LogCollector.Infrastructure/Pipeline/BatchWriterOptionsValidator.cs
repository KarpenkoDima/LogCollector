using Microsoft.Extensions.Options;

namespace LogCollector.Infrastructure.Pipeline;

/// <summary>
/// Validates <see cref="BatchWriterOptions"/> at startup (P1.3).
///
/// Registered with <c>ValidateOnStart()</c> — a bad batch size or timeout crashes
/// the host at boot with a clear message rather than producing degenerate batching
/// behaviour at runtime.
/// </summary>
public sealed class BatchWriterOptionsValidator : IValidateOptions<BatchWriterOptions>
{
    public ValidateOptionsResult Validate(string? name, BatchWriterOptions options)
    {
        var errors = new List<string>();

        // BatchSize: at least 1 (a batch of 0 is meaningless), cap at 10000 to
        // bound transaction size and memory per flush.
        if (options.BatchSize is < 1 or > 10_000)
            errors.Add($"BatchSize must be in [1, 10000], got {options.BatchSize}.");

        // BatchTimeout: strictly positive (0 would spin), at most 30s (beyond that
        // entries stall too long in the channel during low traffic).
        if (options.BatchTimeout <= TimeSpan.Zero)
            errors.Add(
                $"BatchTimeout must be greater than zero, got {options.BatchTimeout}.");
        else if (options.BatchTimeout > TimeSpan.FromSeconds(30))
            errors.Add(
                $"BatchTimeout must not exceed 30 seconds, got {options.BatchTimeout}.");

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
