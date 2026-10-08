using Inventory.Application.Costing;
using Inventory.Application.Imports;
using Inventory.Application.Nayax;
using Inventory.Application.SaleTimestampRepair;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Imports;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Time;

namespace InventoryApi.Tests;

/// <summary>
/// Wires the Nayax sale timestamp repair use cases (issue #472) to their EF adapter, the real
/// uploaded-export reader and the real <c>Australia/Sydney</c> calendar over one
/// <see cref="AppDbContext"/>, the way the production DI container does.
///
/// The calendar is deliberately the production <see cref="SydneyBusinessCalendar"/> rather than a
/// trivial fake: every business date, revenue movement and reconciliation day this workflow reports
/// depends on real daylight-saving behaviour, which a fake identity conversion would hide.
/// </summary>
internal static class TestSaleTimestampRepairUseCases
{
    public static PreviewNayaxSaleTimestampRepair Preview(
        AppDbContext db,
        INayaxLynxClient nayax,
        IClock clock,
        INayaxSalesWorkbookReader? workbook = null) =>
        new(
            new EfNayaxSaleTimestampRepairStore(db),
            nayax,
            workbook ?? new ClosedXmlNayaxSalesWorkbookReader(),
            new SydneyBusinessCalendar(clock),
            clock);

    public static ApplyNayaxSaleTimestampRepair Apply(
        AppDbContext db,
        IAuthenticatedActorAccessor actors,
        IClock clock,
        IRebuildProductCost? rebuild = null) =>
        new(
            new EfNayaxSaleTimestampRepairStore(db),
            rebuild ?? TestCostingUseCases.Rebuild(db),
            actors,
            new SydneyBusinessCalendar(clock),
            clock);
}
