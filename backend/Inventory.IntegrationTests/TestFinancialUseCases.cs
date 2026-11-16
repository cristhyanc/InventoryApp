using Inventory.Application.Commissions;
using Inventory.Application.Nayax;
using Inventory.Application.NayaxProcessingFees;
using Inventory.Application.Time;
using Inventory.Infrastructure.Sites;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Reporting.Persistence;
using Inventory.Infrastructure.Data;
using InventoryApi.Tests.Application.Time;

namespace InventoryApi.Tests;

internal static class TestFinancialUseCases
{
    /// <summary>
    /// The processing fee use case over a test database. The business calendar only matters to the
    /// business-day-bounded period the machine dashboard asks for (issue #310), so the date-range
    /// callers - every report - may leave it at the trivial fake.
    /// </summary>
    public static IGetNayaxProcessingFees ProcessingFees(AppDbContext db, IBusinessCalendar? businessCalendar = null) =>
        new GetNayaxProcessingFees(
            new EfNayaxProcessingFeeFactsProvider(db),
            new EfNayaxFeeRateStore(db),
            businessCalendar ?? TrivialCalendar);

    /// <summary>
    /// The identity business calendar (a UTC date is its own business date) the date-range report
    /// tests use when they assert grouping or tenancy rather than <c>Australia/Sydney</c> behaviour.
    /// A test that needs real Sydney days and daylight-saving transitions uses
    /// <see cref="FixedSydneyTime"/> instead, and passes the same calendar to the fee use case.
    /// </summary>
    public static IBusinessCalendar TrivialCalendar => new FakeBusinessCalendar(new DateTime(2026, 3, 12));

    /// <summary>
    /// The commission report use case over a test database. Since issue #1252 it resolves the
    /// requested dates to business-day instants and each sale's agreement to that sale's business
    /// date, so a test about <c>Australia/Sydney</c> boundaries passes <see cref="FixedSydneyTime"/>'s
    /// calendar; the rest may leave it at the trivial identity fake.
    /// </summary>
    public static GetSiteCommissionReport SiteCommissions(
        AppDbContext db,
        INayaxLynxClient nayax,
        IBusinessCalendar? businessCalendar = null) =>
        new(
            nayax,
            new EfSiteCommissionStore(db),
            new SiteNameResolver(),
            businessCalendar ?? TrivialCalendar);
}
