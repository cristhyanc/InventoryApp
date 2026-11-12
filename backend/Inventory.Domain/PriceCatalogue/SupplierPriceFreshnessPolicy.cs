using Inventory.Domain.Exceptions;

namespace Inventory.Domain.PriceCatalogue;

/// <summary>
/// How old an observed supplier price may be and still be compared (issue #925, parent #817): a price
/// stays current <strong>until the supplier's next scheduled search has had its chance</strong>, and
/// becomes too old only when that search did not refresh it.
///
/// <para><strong>The rule is the supplier's own schedule, not a number of hours</strong> (issue #1200,
/// the owner's decision of 12 November 2026). A price read on a day is current until the end of the
/// first day after it that the supplier is searched on (<see cref="SupplierSearchSchedulePolicy.NextOccurrenceAfter"/>),
/// plus <see cref="MarginHours"/>: a supplier searched on Wednesday and Sunday keeps a Wednesday
/// price until Monday 06:00, and a supplier searched every second Saturday keeps one for the
/// fortnight. What was wrong with a fixed age is that it measured the wrong thing - the longest gap
/// the schedule <em>could</em> have, from whenever the price happened to be read - so a Sunday price
/// outlived a missed Wednesday run, and a price read on an off day got the same allowance wherever
/// the next run fell.</para>
///
/// <para><strong>The dates are the schedule's calendar's and this file resolves none of them.</strong>
/// <see cref="IsFreshUntil"/> is given a true instant that
/// <c>Inventory.Application.Time.ISupplierSearchScheduleCalendar</c> already converted - the start of
/// the day after that next scheduled day - because the zone and its daylight-saving rules are a
/// platform fact rather than a domain rule (AGENTS.md § Reporting dates).</para>
///
/// <para><strong>The margin is why the boundary is the end of the day and not the run's own
/// hour.</strong> A run that starts a little later than yesterday's has not made yesterday's prices
/// wrong, and a price nobody can compare is worse guidance than a price that is a few hours old.</para>
///
/// <para><strong>The hours limit remains a stored description of the cadence and decides
/// nothing.</strong> <see cref="LimitForCheckInterval"/> and the constants below are what
/// <see cref="SupplierSearchSchedulePolicy.FreshnessHoursFor"/> derives
/// <c>SupplierPreset.PriceCheckFreshnessHours</c> from, and that column is still shown to a Super
/// Admin; since issue #1200 no comparison and no review reads it.</para>
///
/// <para><strong>Staleness is the only thing this policy decides, and it decides nothing about
/// money.</strong> No price, GST rate or basis passes through it; <see cref="CheapestUnitOfferPolicy"/>
/// reads its answer as one of the eligibility rules, and nothing recorded - a cost, a valuation, a GST
/// figure or a report - is touched by either.</para>
/// </summary>
public static class SupplierPriceFreshnessPolicy
{
    /// <summary>
    /// The allowance after the scheduled search that should have refreshed a price, in hours - and the
    /// same allowance on top of a supplier's own check interval in the stored hours limit.
    ///
    /// <para>It exists because a collector run is not a clock: a run that starts six hours later than
    /// yesterday's has not made yesterday's prices wrong, and a boundary at the run's own hour would
    /// turn every such run into a page of uncomparable offers.</para>
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

    /// <summary>The gap between two checks made on the same day each week.</summary>
    public const int WeeklyCheckIntervalHours = 7 * 24;

    /// <summary>
    /// The freshness limit of a supplier checked once a week: 174 hours, the owner's decision of
    /// 1 November 2026 that BIG W is checked on Thursdays (its catalogues start then) and Kmart on
    /// Mondays, because both charge per searched result. The BIG W and Kmart records carry it
    /// because <c>SetWeeklySupplierPriceFreshness</c> wrote it to them, not because this policy knows
    /// which suppliers they are.
    /// </summary>
    public const int WeeklyCheckLimitHours = WeeklyCheckIntervalHours + MarginHours;

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
    /// The instant a price stops being compared: <paramref name="nextSearchDayEndUtc"/> plus
    /// <see cref="MarginHours"/>.
    /// </summary>
    /// <param name="nextSearchDayEndUtc">
    /// When the first scheduled search day <em>after</em> the day the price was observed ends - 00:00
    /// of the following date in the schedule's own calendar, as a true instant, which
    /// <c>Inventory.Application.Time.ISupplierSearchScheduleCalendar</c> converts.
    /// </param>
    public static DateTime ComparableUntilUtc(DateTime nextSearchDayEndUtc) =>
        nextSearchDayEndUtc.AddHours(MarginHours);

    /// <summary>
    /// Whether a price whose next scheduled search day ends at <paramref name="nextSearchDayEndUtc"/>
    /// is still fresh at <paramref name="asOfUtc"/>.
    ///
    /// <para><strong>The boundary belongs to the stale side</strong>, exactly as the hours limit it
    /// replaced did: at <see cref="ComparableUntilUtc"/> the search that should have refreshed this
    /// price has had its whole day and six hours more, so the price is too old to compare and the
    /// boundary belongs to one side deliberately rather than by accident.</para>
    ///
    /// <para>A price observed in the future - a clock that disagrees with itself - is fresh, because
    /// its own next scheduled day is in the future too; calling a price nobody has had time to age
    /// "too old" would be the one answer that is certainly wrong.</para>
    /// </summary>
    public static bool IsFreshUntil(DateTime asOfUtc, DateTime nextSearchDayEndUtc) =>
        asOfUtc < ComparableUntilUtc(nextSearchDayEndUtc);
}
