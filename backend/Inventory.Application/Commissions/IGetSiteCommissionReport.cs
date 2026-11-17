using Inventory.Application.Time;

namespace Inventory.Application.Commissions;

/// <summary>
/// A commission period that is bounded by business days rather than by calendar dates: the exact UTC
/// instants its completed sales are selected between (inclusive start, exclusive end), and the first
/// and last business date those instants cover (issue #1250).
///
/// The money reports' periods are of this kind, exactly as
/// <c>NayaxProcessingFeeBusinessPeriod</c> already is for their fees. Their boundaries are
/// business-day midnights, which fall in the middle of a UTC day, so asking for the whole UTC dates
/// they touch would charge a period commission on sales its own revenue excludes - and omit the
/// commission on the sales it does count. The two business dates stay part of the period because an
/// agreement is effective-dated, not an instant.
/// </summary>
public readonly record struct SiteCommissionBusinessPeriod(
    DateTime StartUtc,
    DateTime EndExclusiveUtc,
    DateTime FirstBusinessDate,
    DateTime LastBusinessDate)
{
    /// <summary>
    /// The period an inclusive range of requested <em>business</em> dates names: business-day
    /// midnight at the start of the first date to, exclusively, business-day midnight at the start
    /// of the day after the last - 23, 24 or 25 hours per business day. This is what the standalone
    /// Commission report asks for since issue #1252, through the one business calendar port, so
    /// commission due for a period starts at local midnight rather than at 10 or 11 am.
    /// </summary>
    public static SiteCommissionBusinessPeriod ForBusinessDates(
        IBusinessCalendar businessCalendar, DateTime from, DateTime to)
    {
        ArgumentNullException.ThrowIfNull(businessCalendar);

        var firstBusinessDate = from.Date;
        var lastBusinessDate = to.Date;
        return new(
            businessCalendar.StartOfBusinessDayUtc(firstBusinessDate),
            businessCalendar.StartOfBusinessDayUtc(lastBusinessDate.AddDays(1)),
            firstBusinessDate,
            lastBusinessDate);
    }
}

public interface IGetSiteCommissionReport
{
    /// <summary>
    /// The report for an inclusive range of business dates - the standalone Commission report's
    /// form. Since issue #1252 it resolves those dates to the window below through the business
    /// calendar, so its completed sales are the ones the business days actually cover.
    /// </summary>
    Task<SiteCommissionReport> Handle(
        DateTime from,
        DateTime to,
        long? siteId,
        CancellationToken cancellationToken);

    /// <summary>
    /// The same report for a period bounded by business days, the form every money report asks for
    /// (issue #1250): the commission amounts it returns are calculated over exactly the sales those
    /// reports' revenue counts, instead of over the whole UTC dates the period's instants fall on.
    /// </summary>
    Task<SiteCommissionReport> HandleBusinessPeriod(
        SiteCommissionBusinessPeriod period,
        long? siteId,
        CancellationToken cancellationToken);
}
