using Inventory.Application.Dashboard;
using Inventory.Application.Nayax;
using Inventory.Application.Products;
using Inventory.Application.Reorder;
using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Gst;
using InventoryApi.Tests.Application.Products;
using InventoryApi.Tests.Application.Time;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.Dashboard;

/// <summary>
/// Which figures the home Dashboard summary reports to which role (issue #527).
///
/// <para>The Dashboard itself needs <c>Dashboard.View</c>, which every role holds, so the decision
/// under test here is the second one: a member whose role does not grant
/// <c>Dashboard.ViewFinancials</c> is answered <see cref="RedactedDashboardSummaryDto"/>, which
/// carries neither the sales card nor the inventory valuation. The roles are written out literally
/// from the agreed table of issue #328 rather than read back from <c>RoleCapabilities</c>: a test
/// that asked the table what it allows would pass whatever that table happened to say.</para>
///
/// <para>The sales facts port is a counting fake, which is how "the figures are not calculated for
/// those callers" is asserted as a fact about the use case rather than inferred from the shape it
/// returned.</para>
/// </summary>
public class GetDashboardSummaryFinancialRedactionTests
{
    private const int BusinessId = 7;

    // A Wednesday, so week-to-date is a genuinely partial week.
    private static readonly DateTime NowUtc = new(2026, 10, 7, 9, 30, 0, DateTimeKind.Utc);

    /// <summary>The roles the agreed table grants <c>Dashboard.ViewFinancials</c>.</summary>
    public static TheoryData<BusinessRole> RolesThatSeeFinancials => new()
    {
        BusinessRole.Manager,
        BusinessRole.Owner,
    };

    [Fact]
    public async Task An_operator_is_answered_the_summary_without_its_financial_figures()
    {
        var salesFacts = new FakeDashboardSummarySalesFactsProvider(currentPeriodSales: 1_234.56m);

        var summary = await UseCase(BusinessRole.Operator, salesFacts).Handle(CancellationToken.None);

        var redacted = Assert.IsType<RedactedDashboardSummaryDto>(summary);
        Assert.Equal(NowUtc, redacted.AsOfUtc);
        Assert.Equal(NowUtc.Date, redacted.BusinessDate);
    }

    /// <summary>
    /// The sales facts are never read for such a caller: there is no week-to-date query, so the
    /// redacted figures do not exist anywhere in the request rather than being dropped on the way
    /// out.
    /// </summary>
    [Fact]
    public async Task An_operators_sales_figures_are_not_calculated_at_all()
    {
        var salesFacts = new FakeDashboardSummarySalesFactsProvider(currentPeriodSales: 1_234.56m);

        await UseCase(BusinessRole.Operator, salesFacts).Handle(CancellationToken.None);

        Assert.Equal(0, salesFacts.Calls);
        Assert.Null(salesFacts.CurrentPeriod);
        Assert.Null(salesFacts.PriorPeriod);
    }

    /// <summary>
    /// Redaction removes the two financial figures and changes nothing else: the refill, ordering
    /// and remaining inventory figures an Operator is given are the same ones an Owner is given,
    /// from the same reads.
    /// </summary>
    [Fact]
    public async Task An_operator_is_given_the_same_non_financial_figures_as_an_owner()
    {
        var catalog = new[]
        {
            Product(100, "Coke", quantityInStock: 1, lowStockThreshold: 5, restockTo: 24, inventoryValue: 30m),
            Product(200, "Chips", quantityInStock: 5, inventoryValue: null),
        };
        var fleet = Fleet((1, [Selection(100, par: 10, missing: 10, threshold: 2)]));

        var redacted = Assert.IsType<RedactedDashboardSummaryDto>(
            await UseCase(BusinessRole.Operator, catalog: catalog, nayax: fleet).Handle(CancellationToken.None));
        var full = Assert.IsType<DashboardSummaryDto>(
            await UseCase(BusinessRole.Owner, catalog: catalog, nayax: fleet).Handle(CancellationToken.None));

        Assert.Equal(full.AsOfUtc, redacted.AsOfUtc);
        Assert.Equal(full.BusinessDate, redacted.BusinessDate);
        Assert.Equal(full.NeedsRefill, redacted.NeedsRefill);
        Assert.Equal(full.NeedsOrdering, redacted.NeedsOrdering);
        Assert.Equal(full.Inventory.IsInventoryValueComplete, redacted.Inventory.IsInventoryValueComplete);
        Assert.Equal(full.Inventory.ProductsWithUnknownCost, redacted.Inventory.ProductsWithUnknownCost);
        Assert.Equal(full.Inventory.ProductCount, redacted.Inventory.ProductCount);
        Assert.Equal(full.Inventory.UnitsInStorage, redacted.Inventory.UnitsInStorage);
    }

    /// <summary>
    /// The data-quality half of the inventory card survives redaction. An Operator may know that
    /// some product costs have never been rebuilt - that is a fact about the catalogue, not a
    /// financial figure - without being shown what the stock is worth.
    /// </summary>
    [Fact]
    public async Task An_operator_still_sees_the_inventory_cards_data_quality_figures()
    {
        var catalog = new[]
        {
            Product(100, "Coke", quantityInStock: 12, inventoryValue: 30m),
            Product(200, "Chips", quantityInStock: 5, inventoryValue: null),
        };

        var redacted = Assert.IsType<RedactedDashboardSummaryDto>(
            await UseCase(BusinessRole.Operator, catalog: catalog).Handle(CancellationToken.None));

        Assert.False(redacted.Inventory.IsInventoryValueComplete);
        Assert.Equal(1, redacted.Inventory.ProductsWithUnknownCost);
        Assert.Equal(2, redacted.Inventory.ProductCount);
        Assert.Equal(17, redacted.Inventory.UnitsInStorage);
    }

    [Theory]
    [MemberData(nameof(RolesThatSeeFinancials))]
    public async Task A_role_that_holds_the_financial_capability_is_answered_the_whole_summary(BusinessRole role)
    {
        var salesFacts = new FakeDashboardSummarySalesFactsProvider(
            currentPeriodSales: 1_234.56m, earliestRecordedSaleUtc: NowUtc.AddYears(-1));
        var catalog = new[] { Product(100, "Coke", quantityInStock: 12, inventoryValue: 30m) };

        var summary = await UseCase(role, salesFacts, catalog).Handle(CancellationToken.None);

        var full = Assert.IsType<DashboardSummaryDto>(summary);
        Assert.Equal(1, salesFacts.Calls);
        Assert.Equal(1_234.56m, full.SalesThisWeek.Sales);
        Assert.Equal(30m, full.Inventory.InventoryValueAtCost);
    }

    /// <summary>
    /// The fail-closed direction: a caller whose membership does not resolve has no role to read the
    /// capability from, and is refused rather than redacted. Treating them as the least-privileged
    /// role would hand an unrecognised caller the non-financial half of another business's summary.
    /// </summary>
    [Theory]
    [InlineData(BusinessAccessDenialReason.MembershipMissing)]
    [InlineData(BusinessAccessDenialReason.MembershipInactive)]
    [InlineData(BusinessAccessDenialReason.MembershipAmbiguous)]
    [InlineData(BusinessAccessDenialReason.BusinessInactive)]
    [InlineData(BusinessAccessDenialReason.RoleUnrecognised)]
    public async Task A_caller_whose_membership_does_not_resolve_is_refused_and_reads_nothing(
        BusinessAccessDenialReason reason)
    {
        var salesFacts = new FakeDashboardSummarySalesFactsProvider(currentPeriodSales: 1_234.56m);
        var catalog = new FakeProductCatalogStore(Product(100, "Coke", inventoryValue: 30m));
        var useCase = UseCase(
            FakeCurrentBusinessProvider.Denied(reason), salesFacts, catalog, Fleet(), OutstandingOrders());

        var exception = await Assert.ThrowsAsync<BusinessAccessDeniedException>(
            () => useCase.Handle(CancellationToken.None));

        Assert.Equal(reason, exception.Reason);
        Assert.Equal(0, salesFacts.Calls);
        Assert.Equal(0, catalog.ListUnorderedCalls);
    }

    private static GetDashboardSummary UseCase(
        BusinessRole role,
        IDashboardSummarySalesFactsProvider? salesFacts = null,
        IReadOnlyList<ProductRecord>? catalog = null,
        Mock<INayaxLynxClient>? nayax = null) =>
        UseCase(
            new FakeCurrentBusinessProvider(BusinessId, role),
            salesFacts ?? new FakeDashboardSummarySalesFactsProvider(),
            new FakeProductCatalogStore([.. catalog ?? []]),
            nayax ?? Fleet(),
            OutstandingOrders());

    private static GetDashboardSummary UseCase(
        ICurrentBusinessProvider currentBusiness,
        IDashboardSummarySalesFactsProvider salesFacts,
        IProductCatalogStore catalog,
        Mock<INayaxLynxClient> nayax,
        Mock<IOutstandingSupplierOrderQuantityStore> outstandingOrders) =>
        new(new FakeClock(NowUtc),
            new FakeBusinessCalendar(NowUtc),
            currentBusiness,
            salesFacts,
            catalog,
            new CalculateReorderNeeds(nayax.Object, outstandingOrders.Object));

    private static Mock<IOutstandingSupplierOrderQuantityStore> OutstandingOrders()
    {
        var store = new Mock<IOutstandingSupplierOrderQuantityStore>();
        store.Setup(x => x.GetOutstandingQuantitiesByProductAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<long, decimal>());
        return store;
    }

    private static Mock<INayaxLynxClient> Fleet(
        params (long MachineId, NayaxMachineProduct[] Products)[] machines)
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(machines.Select(m => new NayaxMachine { MachineID = m.MachineId }).ToList());
        foreach (var machine in machines)
        {
            nayax.Setup(x => x.GetMachineProductsAsync(machine.MachineId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(machine.Products.ToList());
        }

        return nayax;
    }

    private static NayaxMachineProduct Selection(long productId, int par, int missing, int threshold) =>
        new() { NayaxProductID = productId, PAR = par, MissingStockByMDB = missing, VendOutAlertThreshold = threshold };

    private static ProductRecord Product(
        long id,
        string name,
        int quantityInStock = 0,
        int lowStockThreshold = 0,
        int restockTo = 0,
        decimal? inventoryValue = 0m) =>
        FakeProductCatalogStore.Product(id, name, quantityInStock, lowStockThreshold, restockTo) with
        {
            InventoryValue = inventoryValue,
        };
}
