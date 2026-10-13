using Inventory.Application.Access;
using Inventory.Application.Dashboard;
using Inventory.Application.Nayax;
using Inventory.Application.Reorder;
using Inventory.Application.Tenancy;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Gst;
using InventoryApi.Tests.Application.SupportSessions;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.Dashboard;

/// <summary>
/// Two-business isolation for every home Dashboard metric (issue #459), relationally, through the
/// real EF adapters and the central <c>AppDbContext</c> query filters.
///
/// The machine fleet is the interesting case, and the reason this is a whole-summary test rather than
/// a per-adapter one: Nayax reports selections for the operator account, so the same fleet read
/// returns machines and product ids belonging to a different business. What keeps the refill and
/// ordering cards inside the tenant boundary is that a selection is only evaluated when the caller's
/// own catalogue holds that product - "not in my catalogue" is the boundary, never a gap to fill in
/// with a fabricated alert.
/// </summary>
public class GetDashboardSummaryTenancyTests : IDisposable
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;

    private const long ProductOfA = 100;
    private const long ProductOfB = 200;

    // Wednesday 7 October 2026, 11:00 in Sydney.
    private static readonly FixedSydneyTime Time =
        new(new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc));

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public GetDashboardSummaryTenancyTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
        setup.Businesses.AddRange(
            new Business { Id = BusinessA, Name = "Vending A", Status = BusinessStatus.Active, CreatedAtUtc = DateTime.UtcNow },
            new Business { Id = BusinessB, Name = "Vending B", Status = BusinessStatus.Active, CreatedAtUtc = DateTime.UtcNow });
        setup.SaveChanges();

        setup.Products.AddRange(
            new Product
            {
                Id = ProductOfA,
                BusinessId = BusinessA,
                Name = "A-Coke",
                QuantityInStock = 12,
                LowStockThreshold = 5,
                RestockTo = 24,
                InventoryValue = 30m,
            },
            new Product
            {
                Id = ProductOfB,
                BusinessId = BusinessB,
                Name = "B-Chips",
                QuantityInStock = 500,
                LowStockThreshold = 5,
                RestockTo = 24,
                InventoryValue = 9_000m,
            });
        setup.NayaxSales.AddRange(
            Sale(BusinessA, 1, Time.Window.CurrentWeek.StartUtc.AddHours(1), 40m),
            Sale(BusinessA, 2, Time.Window.PreviousComparableWeek.StartUtc.AddYears(-1), 1m),
            Sale(BusinessB, 1, Time.Window.CurrentWeek.StartUtc.AddHours(1), 7_000m));
        setup.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static NayaxSales Sale(int businessId, long transactionId, DateTime authorizedUtc, decimal value) =>
        new()
        {
            BusinessId = businessId,
            TransactionID = transactionId,
            MachineID = 10,
            SettlementValue = value,
            PaymentMethod = "Credit",
            MachineAuthorizationTime = authorizedUtc,
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
        };

    /// <summary>
    /// One operator fleet, carrying one machine stocked with business A's product and one stocked
    /// with business B's. Both are empty, so each business must see exactly one machine needing a
    /// refill - its own - and never the other's.
    /// </summary>
    private static Mock<INayaxLynxClient> SharedOperatorFleet()
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new NayaxMachine { MachineID = 1 }, new NayaxMachine { MachineID = 2 }]);
        nayax.Setup(x => x.GetMachineProductsAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new NayaxMachineProduct
            {
                NayaxProductID = ProductOfA, PAR = 10, MissingStockByMDB = 10, VendOutAlertThreshold = 2,
            }]);
        nayax.Setup(x => x.GetMachineProductsAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new NayaxMachineProduct
            {
                NayaxProductID = ProductOfB, PAR = 10, MissingStockByMDB = 10, VendOutAlertThreshold = 2,
            }]);
        return nayax;
    }

    private static GetDashboardSummary UseCase(
        AppDbContext db, ICurrentBusinessProvider currentBusiness, Mock<INayaxLynxClient>? fleet = null)
    {
        var outstandingOrders = new Mock<IOutstandingSupplierOrderQuantityStore>();
        outstandingOrders.Setup(x => x.GetOutstandingQuantitiesByProductAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<long, decimal>());

        return new GetDashboardSummary(
            Time.Clock,
            Time.Calendar,
            new CurrentCapabilities(new FakeSupportSessionAccessor(), currentBusiness),
            new EfDashboardSummarySalesFactsProvider(db),
            new EfProductCatalogStore(db),
            new CalculateReorderNeeds((fleet ?? SharedOperatorFleet()).Object, outstandingOrders.Object));
    }

    private async Task<DashboardSummaryDto> SummaryFor(int businessId)
    {
        await using var db = TestAppDbContext.For(_options, businessId);

        // An Owner of that same business, so the isolation assertions below see every figure; what a
        // role without Dashboard.ViewFinancials is given is issue #527's
        // GetDashboardSummaryRedactionTests.
        var summary = await UseCase(db, new FakeCurrentBusinessProvider(businessId))
            .Handle(CancellationToken.None);

        return Assert.IsType<DashboardSummaryDto>(summary);
    }

    [Fact]
    public async Task Each_business_sees_only_its_own_week_to_date_revenue()
    {
        Assert.Equal(40m, (await SummaryFor(BusinessA)).SalesThisWeek.Sales);
        Assert.Equal(7_000m, (await SummaryFor(BusinessB)).SalesThisWeek.Sales);
    }

    [Fact]
    public async Task Each_business_sees_only_its_own_inventory_valuation_product_count_and_storage_units()
    {
        var businessA = (await SummaryFor(BusinessA)).Inventory;
        var businessB = (await SummaryFor(BusinessB)).Inventory;

        Assert.Equal(30m, businessA.InventoryValueAtCost);
        Assert.Equal(1, businessA.ProductCount);
        Assert.Equal(12, businessA.UnitsInStorage);

        Assert.Equal(9_000m, businessB.InventoryValueAtCost);
        Assert.Equal(1, businessB.ProductCount);
        Assert.Equal(500, businessB.UnitsInStorage);
    }

    /// <summary>
    /// The shared operator fleet reports an empty selection on both machines, but each business only
    /// owns one of the two products. Counting the other business's empty selection would both leak
    /// its stock position and tell this business to go and refill a machine that is not its concern.
    /// </summary>
    [Fact]
    public async Task A_machine_stocked_with_another_businesses_product_never_counts_toward_this_businesses_refill()
    {
        var businessA = (await SummaryFor(BusinessA)).NeedsRefill;
        var businessB = (await SummaryFor(BusinessB)).NeedsRefill;

        Assert.Equal(1, businessA.MachinesNeedingRefill);
        Assert.Equal(1, businessA.EmptySelectionCount);
        Assert.Equal(1, businessA.SelectionsEvaluated);
        Assert.Equal(2, businessA.MachinesEvaluated);

        Assert.Equal(1, businessB.MachinesNeedingRefill);
        Assert.Equal(1, businessB.SelectionsEvaluated);
    }

    /// <summary>
    /// The same applies to ordering: business B's product is well stocked and needs nothing, and the
    /// empty machine carrying it must not make business A's catalogue look short either.
    /// </summary>
    [Fact]
    public async Task Ordering_counts_only_the_callers_own_catalogue()
    {
        var businessA = (await SummaryFor(BusinessA)).NeedsOrdering;
        var businessB = (await SummaryFor(BusinessB)).NeedsOrdering;

        Assert.Equal(1, businessA.ProductsNeedingOrdering);
        Assert.Equal(1, businessA.ProductsEvaluated);

        Assert.Equal(0, businessB.ProductsNeedingOrdering);
        Assert.Equal(1, businessB.ProductsEvaluated);
    }

    /// <summary>
    /// The query filters themselves: a context whose business was never resolved reads nothing
    /// anywhere in the summary, because an unresolved business is never treated as "no filter"
    /// (AGENTS.md § Tenant ownership and data isolation).
    ///
    /// The caller here is deliberately a resolved Owner over a <em>denied</em> context, which is a
    /// combination production cannot produce - since issue #527 a caller whose membership does not
    /// resolve is refused before any read, which
    /// <see cref="A_caller_whose_membership_does_not_resolve_is_refused_and_reads_nothing"/> covers.
    /// What this test still pins is that the central filters, not the use case, are what make an
    /// unscoped read empty.
    /// </summary>
    [Fact]
    public async Task A_denied_context_sees_an_empty_summary()
    {
        await using var db = TestAppDbContext.Denied(_options);
        var summary = Assert.IsType<DashboardSummaryDto>(
            await UseCase(db, new FakeCurrentBusinessProvider(BusinessA)).Handle(CancellationToken.None));

        Assert.Equal(0m, summary.SalesThisWeek.Sales);
        Assert.False(summary.SalesThisWeek.IsComparisonAvailable);
        Assert.Equal(0, summary.NeedsRefill.MachinesNeedingRefill);
        Assert.Equal(0, summary.NeedsRefill.SelectionsEvaluated);
        Assert.Equal(0, summary.NeedsOrdering.ProductsNeedingOrdering);
        Assert.Equal(0, summary.NeedsOrdering.ProductsEvaluated);
        Assert.Equal(0, summary.Inventory.ProductCount);
        Assert.Equal(0, summary.Inventory.UnitsInStorage);
    }

    /// <summary>
    /// What production does instead, relationally: a caller whose membership does not resolve is
    /// refused and nothing is read at all - not the sales, not the catalogue, not the fleet. The
    /// summary needs the caller's role to decide which figures they may be shown (issue #527), and
    /// an unresolved membership is a refusal rather than a fall back to the least-privileged role.
    /// </summary>
    [Fact]
    public async Task A_caller_whose_membership_does_not_resolve_is_refused_and_reads_nothing()
    {
        var fleet = SharedOperatorFleet();
        await using var db = TestAppDbContext.For(_options, BusinessA);

        await Assert.ThrowsAsync<BusinessAccessDeniedException>(
            () => UseCase(db, FakeCurrentBusinessProvider.Denied(), fleet).Handle(CancellationToken.None));

        fleet.Verify(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
