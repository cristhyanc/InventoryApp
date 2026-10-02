using Inventory.Application.Commissions;
using Inventory.Application.Nayax;
using Inventory.Application.NayaxProcessingFees;
using Inventory.Application.Time;
using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;

namespace InventoryApi.Tests;

internal static class TestFinancialUseCases
{
    public static IGetNayaxProcessingFees ProcessingFees(AppDbContext db) =>
        new GetNayaxProcessingFees(
            new EfNayaxProcessingFeeFactsProvider(db),
            new EfNayaxFeeRateStore(db));

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
