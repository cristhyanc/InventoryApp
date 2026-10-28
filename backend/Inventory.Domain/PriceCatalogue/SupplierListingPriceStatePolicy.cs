using Inventory.Domain.Exceptions;

namespace Inventory.Domain.PriceCatalogue;

/// <summary>
/// What one supplier listing records about its own price checks (issue #925, parent #817): the latest
/// accepted observation, when it was last checked successfully, and when its price last changed.
///
/// <para><strong>Three facts, deliberately not one.</strong> "We read this listing again and the price
/// was the same" and "this price is new" are different events, and a single "last updated" column
/// would answer neither question: a per-unit comparison needs to know how old the <em>price</em> is -
/// which is the last successful check, not the last change - while a person deciding whether a
/// supplier has moved needs the last change, which a daily re-read would otherwise keep overwriting.
/// </para>
///
/// <para><strong>A failed or missing check removes nothing.</strong> None of the three is cleared by
/// anything: a collector run that fails, or that never reaches this listing, leaves the latest
/// accepted observation and the last successful check exactly as they were, and
/// <see cref="SupplierPriceFreshnessPolicy"/> is what eventually stops that price being compared. A
/// listing's price is replaced only by a later accepted observation - never by an absence, which is
/// the same restraint the report availability model shows about a missing provider figure.</para>
///
/// <para>The writer is the collector import of #937; this policy is the rule that import and the
/// database both hold.</para>
/// </summary>
public static class SupplierListingPriceStatePolicy
{
    /// <summary>
    /// What a caller whose listing has been checked without holding an observation, or holds one
    /// without having been checked, is told.
    /// </summary>
    public const string CheckAndObservationTogetherMessage =
        "A supplier listing's latest accepted observation and its last successful check are recorded "
            + "together, or neither is.";

    /// <summary>
    /// What a caller who recorded a price change on a listing that holds no observation is told: a
    /// price that was never observed cannot have changed.
    /// </summary>
    public const string PriceChangeNeedsObservationMessage =
        "A supplier listing's last price change belongs to a listing that holds an observation.";

    /// <summary>
    /// Whether a listing's three price-check facts describe one coherent state: it holds a latest
    /// accepted observation exactly when it has been checked successfully, and it records a price
    /// change only while it holds an observation.
    ///
    /// <para>A listing that has never been checked holds none of the three, which is every listing
    /// as #923 shipped them.</para>
    /// </summary>
    public static bool IsConsistentState(
        long? latestObservationId,
        DateTime? lastCheckedAtUtc,
        DateTime? lastPriceChangeAtUtc)
    {
        if (latestObservationId.HasValue != lastCheckedAtUtc.HasValue)
        {
            return false;
        }

        return latestObservationId is not null || lastPriceChangeAtUtc is null;
    }

    /// <summary>
    /// Whether a reading observed at <paramref name="candidateObservedAtUtc"/> replaces the state a
    /// reading observed at <paramref name="currentObservedAtUtc"/> left behind - the listing's
    /// latest accepted observation, its last successful check and its last price change, or the one
    /// reading it is holding pending.
    ///
    /// <para><strong>Current means latest observed, not latest delivered.</strong> The collector
    /// decides for itself how it splits its searches into batches and in which order it sends them,
    /// and two searches of one run can report the same supplier listing; so the order readings
    /// arrive in says nothing about the order they were read in. A listing's price, its freshness
    /// and its last change may only ever move forwards, which is the same sentence this policy's
    /// summary already states about an absence: a listing's price is replaced only by a
    /// <em>later</em> accepted observation. A reading that was read earlier than the one already
    /// recorded is kept - it is history, and an accepted one is still an accepted price in
    /// <c>SupplierPriceObservations</c> - but it is not what this listing currently costs.</para>
    ///
    /// <para><strong>Equal instants keep what is already recorded</strong>, deliberately and by one
    /// stated rule rather than by whichever row happened to be processed last: two readings of one
    /// listing bearing the same observed instant are the same check as far as this catalogue can
    /// tell, so the first one recorded stands and the second changes nothing. That makes a
    /// resubmitted batch, and a batch whose rows were reordered in transit, leave exactly the same
    /// state.</para>
    /// </summary>
    /// <param name="currentObservedAtUtc">
    /// When the reading this listing currently holds was observed, or <see langword="null"/> while
    /// it holds none - in which case the candidate is the first and replaces nothing.
    /// </param>
    /// <param name="candidateObservedAtUtc">When the reading being considered was observed.</param>
    public static bool ReplacesCurrentReading(
        DateTime? currentObservedAtUtc,
        DateTime candidateObservedAtUtc) =>
        currentObservedAtUtc is not { } current || candidateObservedAtUtc > current;

    /// <summary>Refuses a listing whose three price-check facts do not describe one coherent state.</summary>
    /// <exception cref="DomainValidationException">
    /// The observation and the check instant disagree, or a price change is recorded with no
    /// observation. Nothing is stored.
    /// </exception>
    public static void RequireConsistentState(
        long? latestObservationId,
        DateTime? lastCheckedAtUtc,
        DateTime? lastPriceChangeAtUtc)
    {
        if (latestObservationId.HasValue != lastCheckedAtUtc.HasValue)
        {
            throw new DomainValidationException(CheckAndObservationTogetherMessage);
        }

        if (latestObservationId is null && lastPriceChangeAtUtc is not null)
        {
            throw new DomainValidationException(PriceChangeNeedsObservationMessage);
        }
    }
}
