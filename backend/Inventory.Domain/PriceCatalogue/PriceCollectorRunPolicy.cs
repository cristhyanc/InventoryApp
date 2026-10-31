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

    /// <summary>
    /// Closed without the collector ever reporting what it did (issue #924): the run was still open
    /// when its expiry passed, or when a later run was issued.
    ///
    /// <para>It is deliberately its own outcome rather than a <see cref="Failed"/> one. A failed run
    /// is one that reported failing; an expired run reported nothing at all, and the difference is
    /// exactly what a reviewer of "did the collector run last night?" needs. Like every finished
    /// status it records an end instant - the instant it was expired, not the instant it should have
    /// expired - and like every other status it removes nothing: the work items it issued keep their
    /// own outcomes, and the listings it never reached keep their last accepted observation.</para>
    /// </summary>
    Expired = 5,
}

/// <summary>
/// How a closed run's searches turned out, one count per outcome (issue #937, parent #817).
///
/// <para>Five counts rather than a success total and a failure total, for the reason the outcomes
/// themselves are five: a search that found nothing, one that was cut short and one that never
/// happened are three different facts about a night's collection, and the only way to read "did the
/// collector run properly?" afterwards is to see them apart.</para>
/// </summary>
/// <param name="Complete">Searched, and everything the supplier publishes was accounted for.</param>
/// <param name="Truncated">Searched, and cut short.</param>
/// <param name="NoMatches">Searched, and this supplier publishes nothing that matches.</param>
/// <param name="Failed">Attempted and could not be completed.</param>
/// <param name="NotSearched">Issued and never answered - the run closed with it still empty.</param>
public sealed record PriceCollectorRunOutcomeCounts(
    int Complete,
    int Truncated,
    int NoMatches,
    int Failed,
    int NotSearched)
{
    /// <summary>A run with no work at all, which is the state every run is created in.</summary>
    public static PriceCollectorRunOutcomeCounts None { get; } = new(0, 0, 0, 0, 0);

    /// <summary>The searches that accounted for everything they set out to: complete, or no match.</summary>
    public int FullyAccounted => Complete + NoMatches;

    /// <summary>And the ones that did not, for whichever of the three reasons.</summary>
    public int ShortOfComplete => Truncated + Failed + NotSearched;

    /// <summary>How many searches the run issued, which is every work item it holds.</summary>
    public int Total => FullyAccounted + ShortOfComplete;
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

    /// <summary>
    /// How long an issued run stays open unless the collector reports back, in hours (issue #924):
    /// six, the owner's default, overridden per deployment by the collector's own App Service
    /// setting.
    ///
    /// <para>It is a lifetime rather than a deadline because a run is issued by this application and
    /// finished by a separate scheduled process: the expiry is what stops a collector that died
    /// mid-run from leaving work nobody will ever account for, and six hours is comfortably longer
    /// than a whole catalogue takes to search while still being the same day's work.</para>
    /// </summary>
    public const int DefaultExpiryHours = 6;

    /// <summary>
    /// The longest run lifetime this application accepts: seven days. It bounds a configured value
    /// rather than expressing a rule - a run still open a week after it was issued is not a run - and
    /// it is generous enough that a deliberately slow backfill would fit.
    /// </summary>
    public const int MaxExpiryHours = 24 * 7;

    /// <summary>What a deployment whose configured run lifetime is outside the accepted range is told.</summary>
    public const string ExpiryOutOfRangeMessage =
        "A price collector run's lifetime must be between 1 and 168 hours.";

    /// <summary>Whether <paramref name="expiryHours"/> is a run lifetime this application accepts.</summary>
    public static bool IsAcceptedExpiry(int expiryHours) => expiryHours is > 0 and <= MaxExpiryHours;

    /// <summary>
    /// <paramref name="expiryHours"/> as it is to be used, refusing anything outside the accepted
    /// range. Zero is refused rather than read as "never expires" or "expires immediately": an
    /// absent lifetime would be a silently wrong answer in whichever direction it was resolved.
    /// </summary>
    /// <exception cref="DomainValidationException">The lifetime is not in the accepted range.</exception>
    public static int RequireExpiryHours(int expiryHours) =>
        IsAcceptedExpiry(expiryHours)
            ? expiryHours
            : throw new DomainValidationException(ExpiryOutOfRangeMessage);

    /// <summary>
    /// When a run issued at <paramref name="issuedAtUtc"/> stops being open, under a lifetime of
    /// <paramref name="expiryHours"/> hours.
    /// </summary>
    /// <exception cref="DomainValidationException">The lifetime is not in the accepted range.</exception>
    public static DateTime ExpiresAt(DateTime issuedAtUtc, int expiryHours) =>
        issuedAtUtc.AddHours(RequireExpiryHours(expiryHours));

    /// <summary>
    /// Whether a run that expires at <paramref name="expiresAtUtc"/> has expired by
    /// <paramref name="asOfUtc"/>.
    ///
    /// <para>The expiry instant belongs to the expired side, the same way
    /// <see cref="SupplierPriceFreshnessPolicy.IsFresh"/>'s limit belongs to the stale side: "six
    /// hours" is the answer to "how long may this run stay open", and the boundary has to belong to
    /// one side deliberately rather than by accident.</para>
    /// </summary>
    public static bool HasExpired(DateTime expiresAtUtc, DateTime asOfUtc) => asOfUtc >= expiresAtUtc;

    /// <summary>What a caller who tried to close a run that had already finished is told.</summary>
    public const string AlreadyFinishedMessage =
        "This price collector run has already finished.";

    /// <summary>
    /// How a run that is being closed ended, from what became of its searches (issue #937).
    ///
    /// <para>A run with nothing short of complete <see cref="PriceCollectorRunStatus.Succeeded"/>,
    /// one with nothing fully accounted for <see cref="PriceCollectorRunStatus.Failed"/>, and
    /// anything between the two <see cref="PriceCollectorRunStatus.PartiallySucceeded"/>. A
    /// <see cref="PriceCollectorWorkItemOutcome.Truncated"/> search counts as short of complete and
    /// not as a success, because it is a search that stopped before it had seen everything - the
    /// same reason it never records a listing as not found.</para>
    ///
    /// <para>A run that was issued no work at all succeeded: it accounted for everything it set out
    /// to check, which was nothing. Reading that as a failure would make "there was nothing due
    /// tonight" indistinguishable from "the collector could not reach a single supplier".</para>
    /// </summary>
    public static PriceCollectorRunStatus StatusFor(PriceCollectorRunOutcomeCounts counts)
    {
        ArgumentNullException.ThrowIfNull(counts);

        if (counts.ShortOfComplete == 0)
        {
            return PriceCollectorRunStatus.Succeeded;
        }

        return counts.FullyAccounted == 0
            ? PriceCollectorRunStatus.Failed
            : PriceCollectorRunStatus.PartiallySucceeded;
    }

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
