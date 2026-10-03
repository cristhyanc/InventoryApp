using Inventory.Application.Commissions;
using Inventory.Application.Nayax;
using Inventory.Application.NayaxProcessingFees;
using Inventory.Application.Time;
using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
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
            businessCalendar ?? new FakeBusinessCalendar(new DateTime(2026, 3, 12)));

    public static GetSiteCommissionReport SiteCommissions(
        AppDbContext db,
        INayaxLynxClient nayax,
        IBusinessCalendar businessCalendar) =>
        new(
            nayax,
            new EfSiteCommissionStore(db),
            new SiteNameResolverAdapter(),
            businessCalendar);
}
