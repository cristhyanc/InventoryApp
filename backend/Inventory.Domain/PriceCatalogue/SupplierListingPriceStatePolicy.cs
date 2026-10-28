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

    /// <summary>
    /// The price-check state a listing is left in by the readings one collector run has accepted for
    /// it: the latest accepted observation, its last successful check and its last price change.
    ///
    /// <para><strong>A replay of the run's timeline, not of its deliveries.</strong> The readings
    /// are walked in the order they were <em>observed</em>, starting from the price the listing held
    /// before the run, so the answer depends only on which readings the run accepted and never on
    /// how the collector split or ordered them. The latest observed reading is the current one
    /// (<see cref="ReplacesCurrentReading"/>: of two bearing the same instant, the one recorded
    /// first stands); the change instant is the first reading of the trailing stretch at that price,
    /// so a run that moved a price and then re-read it unchanged records the change at the first of
    /// the two instants, and a run that only re-read the price it started with leaves the change
    /// where it was.</para>
    /// </summary>
    /// <param name="priceBeforeRun">
    /// The pack price of the listing's latest accepted observation from before this run, or
    /// <see langword="null"/> while it held none.
    /// </param>
    /// <param name="priceChangeBeforeRunUtc">
    /// The listing's last price change as it stands - which is its value from before the run
    /// whenever the run found nothing but that price.
    /// </param>
    /// <param name="runReadings">Every reading this run has accepted for the listing. At least one.</param>
    public static SupplierListingPriceState StateAfterRun(
        decimal? priceBeforeRun,
        DateTime? priceChangeBeforeRunUtc,
        IReadOnlyCollection<AcceptedSupplierReading> runReadings)
    {
        ArgumentNullException.ThrowIfNull(runReadings);

        if (runReadings.Count == 0)
        {
            throw new ArgumentException("A run's state needs at least one accepted reading.", nameof(runReadings));
        }

        // Observed order, and among equal instants the reading recorded first walked last, so it is
        // the one that stands.
        var timeline = runReadings
            .OrderBy(reading => reading.ObservedAtUtc)
            .ThenByDescending(reading => reading.ObservationId);

        var price = priceBeforeRun;
        var changedAtUtc = priceChangeBeforeRunUtc;
        AcceptedSupplierReading? current = null;

        foreach (var reading in timeline)
        {
            if (price != reading.PackPrice)
            {
                changedAtUtc = reading.ObservedAtUtc;
            }

            price = reading.PackPrice;
            current = reading;
        }

        return new SupplierListingPriceState(current!.ObservationId, current.ObservedAtUtc, changedAtUtc);
    }

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

/// <summary>One accepted reading of a supplier listing, as <see cref="SupplierListingPriceStatePolicy.StateAfterRun"/> replays it.</summary>
public sealed record AcceptedSupplierReading(long ObservationId, DateTime ObservedAtUtc, decimal PackPrice);

/// <summary>A listing's three price-check facts (<see cref="SupplierListingPriceStatePolicy"/>).</summary>
public sealed record SupplierListingPriceState(
    long LatestObservationId,
    DateTime LastCheckedAtUtc,
    DateTime? LastPriceChangeAtUtc);
