using Inventory.Domain.Exceptions;

namespace Inventory.Domain.PriceCatalogue;

/// <summary>
/// How one price collector run ended (issue #925, parent #817).
///
/// <para><strong>A run that is still going is not a run that succeeded</strong>, and a run that
/// failed is not a run that found nothing: each is a distinct recorded outcome, in the same way the
/// report availability model keeps <c>Missing</c> apart from a measured zero. The counts a run
/// carries are only ever read beside its status for that reason.</para>
///
/// <para>Numbered from 1, so a stored <c>0</c> is never read as the first declared outcome.</para>
/// </summary>
public enum PriceCollectorRunStatus
{
    /// <summary>Started and not finished. It has no end instant yet.</summary>
    Running = 1,

    /// <summary>Finished, having accounted for everything it set out to check.</summary>
    Succeeded = 2,

    /// <summary>
    /// Finished, having checked some listings and not others - one retailer unreachable, one page
    /// unreadable. The observations it did record are real; the listings it never reached keep the
    /// prices they already had, and go stale on their own freshness limits.
    /// </summary>
    PartiallySucceeded = 3,

    /// <summary>Finished having accounted for nothing. It records no price at all.</summary>
    Failed = 4,
}

/// <summary>
/// The rules of one price collector run (issue #925, parent #817): when it started, when it ended,
/// how it ended and how much it accounted for.
///
/// <para><strong>A run is the provenance of every price it recorded.</strong> Each observation names
/// the run that read it, which is what makes "where did this price come from, and was that read
/// trustworthy?" answerable afterwards - and what lets a failed or partial run be told apart from a
/// supplier that genuinely has no price.</para>
///
/// <para><strong>A failed run removes nothing.</strong> This policy writes nothing and its statuses
/// decide nothing about a listing's stored price: a listing keeps its latest accepted observation and
/// its last successful check time until a <em>later</em> accepted observation replaces them, so a
/// failed check leaves the last good price exactly where it was and
/// <see cref="SupplierPriceFreshnessPolicy"/> is what eventually makes that price uncomparable. That
/// is the owner's own constraint of 27 October 2026: a listing missing from a day's results keeps its
/// last good price until it passes its freshness limit.</para>
/// </summary>
public static class PriceCollectorRunPolicy
{
    /// <summary>What a caller who submitted a run status that is not a declared one is told.</summary>
    public static string UnknownStatusMessage { get; } =
        "A price collector run's status must be one of "
            + string.Join(", ", Enum.GetNames<PriceCollectorRunStatus>())
            + ".";

    /// <summary>What a caller whose finished run has no end instant, or whose running one has one, is told.</summary>
    public const string EndInstantMismatchMessage =
        "A finished price collector run records when it ended, and a running one records no end.";

    /// <summary>What a caller whose run ended before it started is told.</summary>
    public const string EndBeforeStartMessage =
        "A price collector run cannot end before it started.";

    /// <summary>What a caller who submitted a negative count is told.</summary>
    public const string NegativeCountMessage =
        "A price collector run's counts cannot be negative.";

    /// <summary>Whether <paramref name="status"/> is one of the declared outcomes.</summary>
    public static bool IsDeclaredStatus(PriceCollectorRunStatus status) => Enum.IsDefined(status);

    /// <summary>
    /// Whether <paramref name="status"/> is an outcome a run has finished with.
    /// <see cref="PriceCollectorRunStatus.Running"/> is the only one that is not.
    /// </summary>
    public static bool IsFinished(PriceCollectorRunStatus status) =>
        status != PriceCollectorRunStatus.Running;

    /// <summary>
    /// Refuses a run whose status, instants and counts do not describe one coherent run: an
    /// undeclared status, a finished run with no end instant or a running one that has one, an end
    /// before the start, or a negative count.
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// The run is not coherent. Nothing is stored.
    /// </exception>
    public static void RequireConsistentRun(
        PriceCollectorRunStatus status,
        DateTime startedAtUtc,
        DateTime? completedAtUtc,
        int listingsCheckedCount,
        int observationsRecordedCount,
        int failureCount)
    {
        if (!IsDeclaredStatus(status))
        {
            throw new DomainValidationException(UnknownStatusMessage);
        }

        if (IsFinished(status) != completedAtUtc.HasValue)
        {
            throw new DomainValidationException(EndInstantMismatchMessage);
        }

        if (completedAtUtc is not null && completedAtUtc < startedAtUtc)
        {
            throw new DomainValidationException(EndBeforeStartMessage);
        }

        if (listingsCheckedCount < 0 || observationsRecordedCount < 0 || failureCount < 0)
        {
            throw new DomainValidationException(NegativeCountMessage);
        }
    }
}
