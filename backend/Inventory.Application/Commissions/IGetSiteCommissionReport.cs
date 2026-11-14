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
/// agreement, a recorded payment and a payment due date are effective-dated, not instants.
/// </summary>
public readonly record struct SiteCommissionBusinessPeriod(
    DateTime StartUtc,
    DateTime EndExclusiveUtc,
    DateTime FirstBusinessDate,
    DateTime LastBusinessDate)
{
    /// <summary>
    /// The period an inclusive range of plain calendar dates names when those dates are read as UTC
    /// instants: midnight UTC at the start of the first date to, exclusively, midnight UTC at the
    /// start of the day after the last. This is what the standalone Commission report asks for until
    /// issue #1252 migrates it, so <see cref="IGetSiteCommissionReport.Handle"/> keeps answering it
    /// exactly as before.
    /// </summary>
    public static SiteCommissionBusinessPeriod ForCalendarDates(DateTime from, DateTime to) =>
        new(from.Date, to.Date.AddDays(1), from.Date, to.Date);
}

public interface IGetSiteCommissionReport
{
    /// <summary>
    /// The report for an inclusive range of calendar dates, with its completed sales selected
    /// between those dates read as UTC instants - the standalone Commission report's form.
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
