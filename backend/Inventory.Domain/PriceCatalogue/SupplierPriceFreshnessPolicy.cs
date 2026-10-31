using Inventory.Domain.Exceptions;

namespace Inventory.Domain.PriceCatalogue;

/// <summary>
/// How old an observed supplier price may be and still be compared (issue #925, parent #817): one
/// limit per supplier master record, measured from the instant the price was observed.
///
/// <para><strong>A limit is the longest expected gap between two checks, plus a margin.</strong> The
/// owner's decision of 27 October 2026 is two numbers - 30 hours for a supplier checked daily, and
/// 102 hours for one checked on Thursday and Sunday, whose longest gap (Sunday to Thursday) is 96 -
/// and both are the gap plus <see cref="MarginHours"/>. The margin is the whole reason the limit is
/// not the gap itself: a run that starts a little later than yesterday's would otherwise mark every
/// price it has not re-read yet as stale, and a price nobody can compare is worse guidance than a
/// price that is a few hours old.</para>
///
/// <para><strong>The limit is stored per supplier master record, never decided from a supplier's
/// name.</strong> <c>SupplierPreset.PriceCheckFreshnessHours</c> is where it lives, so a supplier
/// whose collection schedule changes is an edit to a row rather than a change to this file, and a
/// supplier added later needs no code at all. The two constants here are the declared cadences those
/// stored values come from - a default for a daily supplier and the alternative for a twice-weekly
/// one - and <see cref="LimitForCheckInterval"/> is how a third cadence would be derived from its own
/// gap rather than invented.</para>
///
/// <para><strong>Staleness is the only thing this policy decides, and it decides nothing about
/// money.</strong> No price, GST rate or basis passes through it; <see cref="CheapestUnitOfferPolicy"/>
/// reads its answer as one of the eligibility rules, and nothing recorded - a cost, a valuation, a GST
/// figure or a report - is touched by either.</para>
/// </summary>
public static class SupplierPriceFreshnessPolicy
{
    /// <summary>
    /// The allowance on top of a supplier's own check interval, in hours.
    ///
    /// <para>It exists because a collector run is not a clock: a run that starts six hours later than
    /// yesterday's has not made yesterday's prices wrong, and a limit equal to the interval exactly
    /// would turn every such run into a page of uncomparable offers.</para>
    /// </summary>
    public const int MarginHours = 6;

    /// <summary>The gap between two daily checks.</summary>
    public const int DailyCheckIntervalHours = 24;

    /// <summary>
    /// The longest gap between two checks made on Thursday and Sunday: Sunday to Thursday, which is
    /// four days. The shorter Thursday-to-Sunday gap is not the limit, because the limit has to cover
    /// the longest wait a price can legitimately have had.
    /// </summary>
    public const int TwiceWeeklyCheckIntervalHours = 96;

    /// <summary>
    /// The freshness limit of a supplier checked daily: 30 hours, the owner's decision of 27 October
    /// 2026. It is also the limit a supplier master record carries until somebody records a different
    /// cadence for it, because daily is what the collector does by default.
    /// </summary>
    public const int DailyCheckLimitHours = DailyCheckIntervalHours + MarginHours;

    /// <summary>
    /// The freshness limit of a supplier checked twice a week: 102 hours, the owner's decision of
    /// 27 October 2026, and the limit the BIG W record carries - because that record says so, not
    /// because this policy knows which supplier it is.
    /// </summary>
    public const int TwiceWeeklyCheckLimitHours = TwiceWeeklyCheckIntervalHours + MarginHours;

    /// <summary>
    /// The longest limit this application stores: 30 days. It bounds a submitted or stored value
    /// rather than expressing a rule - a price that may be five weeks old is not purchasing guidance -
    /// and it is generous enough that a monthly cadence would still fit.
    /// </summary>
    public const int MaxLimitHours = 24 * 30;

    /// <summary>What a caller who submitted a freshness limit outside the accepted range is told.</summary>
    public const string LimitOutOfRangeMessage =
        "A supplier's price freshness limit must be between 1 and 720 hours.";

    /// <summary>
    /// The limit for a supplier checked every <paramref name="checkIntervalHours"/> hours: that
    /// interval plus <see cref="MarginHours"/>.
    ///
    /// <para>It is the derivation behind both constants above - 24 becomes 30 and 96 becomes 102 - so a
    /// cadence nobody has needed yet is worked out the same way rather than guessed.</para>
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// The interval is not positive, or the limit it produces is above <see cref="MaxLimitHours"/>.
    /// </exception>
    public static int LimitForCheckInterval(int checkIntervalHours) =>
        checkIntervalHours > 0
            ? RequireLimit(checkIntervalHours + MarginHours)
            : throw new DomainValidationException(LimitOutOfRangeMessage);

    /// <summary>Whether <paramref name="limitHours"/> is a limit this application accepts.</summary>
    public static bool IsAcceptedLimit(int limitHours) =>
        limitHours is > 0 and <= MaxLimitHours;

    /// <summary>
    /// <paramref name="limitHours"/> as it is to be stored, refusing anything outside the accepted
    /// range. A limit of zero is refused rather than read as "never stale" or "always stale": an
    /// absent rule would be a silently wrong answer in whichever direction it was resolved.
    /// </summary>
    /// <exception cref="DomainValidationException">The limit is not in the accepted range.</exception>
    public static int RequireLimit(int limitHours) =>
        IsAcceptedLimit(limitHours)
            ? limitHours
            : throw new DomainValidationException(LimitOutOfRangeMessage);

    /// <summary>
    /// Whether a price observed at <paramref name="observedAtUtc"/> is still fresh at
    /// <paramref name="asOfUtc"/> under a limit of <paramref name="limitHours"/> hours.
    ///
    /// <para><strong>The limit is the age at which a price stops being usable</strong>, so an
    /// observation exactly <paramref name="limitHours"/> old is stale: "30 hours" is the owner's
    /// answer to "how long is this price good for", and the boundary has to belong to one side
    /// deliberately rather than by accident.</para>
    ///
    /// <para>An observation whose instant is in the future - a clock that disagrees with itself - is
    /// fresh, because the alternative is to call a price nobody has had time to age "too old".</para>
    /// </summary>
    public static bool IsFresh(DateTime observedAtUtc, DateTime asOfUtc, int limitHours) =>
        asOfUtc - observedAtUtc < TimeSpan.FromHours(RequireLimit(limitHours));
}
