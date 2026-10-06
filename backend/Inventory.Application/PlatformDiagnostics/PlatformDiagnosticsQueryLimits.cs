namespace Inventory.Application.PlatformDiagnostics;

/// <summary>
/// The server-side hard maxima a platform-admin diagnostics query runs under (issue #336).
///
/// Every value here is a ceiling, never a default a request can raise. A caller submits SQL and
/// nothing else: there is no page size, no row count, no timeout and no "expand" parameter on the
/// endpoint, so there is no input that could reach these numbers at all.
///
/// <see cref="Create"/> exists so a host can make the ceilings <em>tighter</em> - the test suite
/// uses it to prove interruption and truncation without waiting the full five seconds or seeding a
/// megabyte - and it clamps with <see cref="Math.Min(int, int)"/>, so a configuration mistake or a
/// future caller asking for more gets the hard maximum rather than what it asked for.
/// </summary>
public sealed record PlatformDiagnosticsQueryLimits
{
    /// <summary>
    /// 16 KiB of UTF-8, checked before preparation. Diagnostic SQL over the seven-table surface is
    /// a few hundred bytes; anything approaching this is not an investigation query, and rejecting
    /// it before it reaches SQLite keeps an oversized statement out of the parser entirely.
    /// </summary>
    public const int MaxSqlBytes = 16 * 1024;

    /// <summary>Result rows read, after which the response is truncated and says so.</summary>
    public const int HardMaxRows = 500;

    /// <summary>
    /// 1 MiB for the complete serialised response body, counted as JSON bytes while rows are read
    /// so reading stops at the cap instead of discovering it after the fact.
    /// </summary>
    public const int HardMaxResponseBytes = 1_048_576;

    /// <summary>
    /// Everything the query costs: preparation, execution and result reading together, not the
    /// HTTP wait around them.
    /// </summary>
    public static readonly TimeSpan HardMaxDuration = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Deducted from the byte budget for the response envelope - outcome, column names, row count,
    /// truncation flag, duration, fingerprint and JSON punctuation - so the budget the row reader
    /// spends is what is actually left for rows, and the finished body stays under the cap rather
    /// than under it plus an envelope.
    /// </summary>
    public const int ResponseEnvelopeReserveBytes = 4096;

    private PlatformDiagnosticsQueryLimits(int maxRows, int maxResponseBytes, TimeSpan maxDuration)
    {
        MaxRows = maxRows;
        MaxResponseBytes = maxResponseBytes;
        MaxDuration = maxDuration;
    }

    public int MaxRows { get; }

    public int MaxResponseBytes { get; }

    public TimeSpan MaxDuration { get; }

    /// <summary>The byte budget available to result rows once the envelope is reserved.</summary>
    public int RowByteBudget => Math.Max(0, MaxResponseBytes - ResponseEnvelopeReserveBytes);

    /// <summary>The hard maxima, which is what the application runs with.</summary>
    public static PlatformDiagnosticsQueryLimits Default { get; } =
        new(HardMaxRows, HardMaxResponseBytes, HardMaxDuration);

    /// <summary>
    /// Limits clamped to the hard maxima. A <c>null</c> argument means "use the hard maximum", and
    /// a larger one is reduced to it; the lower bounds (one row, one byte, one tick) stop a
    /// zero or negative value from producing a query that can never return anything.
    /// </summary>
    public static PlatformDiagnosticsQueryLimits Create(
        int? maxRows = null,
        int? maxResponseBytes = null,
        TimeSpan? maxDuration = null) =>
        new(
            Math.Clamp(maxRows ?? HardMaxRows, 1, HardMaxRows),
            Math.Clamp(maxResponseBytes ?? HardMaxResponseBytes, 1, HardMaxResponseBytes),
            TimeSpan.FromTicks(Math.Clamp(
                (maxDuration ?? HardMaxDuration).Ticks,
                1,
                HardMaxDuration.Ticks)));
}
