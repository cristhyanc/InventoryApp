using Inventory.Application.Reporting.Shared;
using Inventory.Domain.Reporting.Availability;

namespace InventoryApi.Tests.Application.Reporting.Shared;

/// <summary>
/// The coverage records the report use-case tests hand their fake facts providers (issue #731).
///
/// <para>The availability policy compares a period with the coverage recorded for it and reads
/// nothing else, so these helpers set the period explicitly instead of trying to mirror whichever
/// dates a particular test asked its report for. <see cref="Complete"/> is deliberately the default
/// a fake uses: a test about something else must not be incidentally about coverage, and a report
/// whose figures nothing accounted for would otherwise add an availability note to every one of
/// them.</para>
/// </summary>
internal static class TestReportCoverage
{
    /// <summary>The period these helpers decide, wider than every period the tests report on.</summary>
    internal static readonly DateTime PeriodStart = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>That period's exclusive end.</summary>
    internal static readonly DateTime PeriodEnd = new(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A range that spans the whole decided period, from end to end.</summary>
    internal static readonly ReportCoverageRange Full = new(PeriodStart, PeriodEnd);

    /// <summary>Both families fully accounted for: the only state in which a zero is a real zero.</summary>
    internal static ReportCoverageFacts Complete() => new()
    {
        PeriodStartUtc = PeriodStart,
        PeriodEndExclusiveUtc = PeriodEnd,
        SalesCoverage = [Full],
        SettlementCoverage = [Full],
        ContainsSalesRows = true,
        ContainsSettlementRows = true,
    };

    /// <summary>
    /// No coverage at all and no data at all: the "no sales data" case, which must read as missing
    /// rather than as a period that measured zero.
    /// </summary>
    internal static ReportCoverageFacts Missing() => new()
    {
        PeriodStartUtc = PeriodStart,
        PeriodEndExclusiveUtc = PeriodEnd,
        SalesCoverage = [],
        SettlementCoverage = [],
        ContainsSalesRows = false,
        ContainsSettlementRows = false,
    };

    /// <summary>
    /// Covered without a gap from the period start up to <paramref name="asOfUtc"/> and no further -
    /// the shape previously imported sales leave behind after a provider is disconnected. The data
    /// is still there, and still reported, labelled "as of" that instant.
    /// </summary>
    internal static ReportCoverageFacts StaleSales(DateTime asOfUtc) => Complete() with
    {
        SalesCoverage = [new ReportCoverageRange(PeriodStart, asOfUtc)],
    };
}
