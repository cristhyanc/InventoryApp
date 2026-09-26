namespace Inventory.Infrastructure.Nayax;

/// <summary>
/// Tunable parameters for <see cref="NayaxResilienceHandler"/>. Not read from configuration: these
/// are fixed engineering constants for a specific known upstream, not a per-environment setting, so
/// there is nothing here for <c>appsettings.json</c>/README to document. Kept as a plain options
/// object (rather than literals in the handler) purely so tests can exercise the same policy shape
/// with faster timings instead of waiting out production-sized delays.
/// </summary>
public sealed class NayaxResilienceOptions
{
    /// <summary>Bound on a single HTTP attempt. A hung connection fails this fast rather than tying up the caller indefinitely.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Retries beyond the first attempt, applied only to idempotent (GET/HEAD) requests.</summary>
    public int MaxRetryAttempts { get; init; } = 3;

    /// <summary>Base exponential-backoff delay between retries (jittered).</summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Fraction of sampled outcomes that must be transient failures before the circuit opens.</summary>
    public double CircuitBreakerFailureRatio { get; init; } = 0.5;

    /// <summary>Minimum sampled outcomes before the circuit breaker's failure ratio is evaluated.</summary>
    public int CircuitBreakerMinimumThroughput { get; init; } = 8;

    /// <summary>Rolling window the circuit breaker's failure ratio is measured over.</summary>
    public TimeSpan CircuitBreakerSamplingDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the circuit stays open (failing fast) before allowing a probe call.</summary>
    public TimeSpan CircuitBreakerBreakDuration { get; init; } = TimeSpan.FromSeconds(15);

    public static readonly NayaxResilienceOptions Default = new();
}
