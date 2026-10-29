using Inventory.Application.Access;
using Inventory.Application.Dashboard;
using Inventory.Application.Nayax;
using Inventory.Application.Reorder;
using Inventory.Application.Tenancy;
using Inventory.Domain.Nayax;
using Inventory.Domain.Subscriptions;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Access;
using InventoryApi.Tests.Application.Gst;
using InventoryApi.Tests.Application.Nayax;
using InventoryApi.Tests.Application.Products;
using InventoryApi.Tests.Application.SupportSessions;
using InventoryApi.Tests.Application.Time;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.Dashboard;

/// <summary>
/// The Dashboard summary for a business without a usable sales provider (issue #726, the #328
/// release gate).
///
/// <para>Nayax is an optional add-on; storage management is what this application is for. So the
/// summary of a business whose connection is unusable - or whose Nayax account has not granted the
/// fleet reads - is still the whole local one, and only the one section that is made entirely of
/// machine figures is withheld: <c>needsRefill</c> carries <c>{ available: false, reason }</c>,
/// <c>needsOrdering</c> carries a real storage-only count labelled with its mode, and the week's
/// sales and the inventory card are untouched. The serialized shape is asserted through the real
/// request pipeline in <c>InventoryApi.Tests.Controllers.ProviderOptionalReorderApiTests</c>.</para>
/// </summary>
public class GetDashboardSummaryProviderOptionalTests
{
    // A Wednesday, so week-to-date is a genuinely partial week.
    private static readonly DateTime NowUtc = new(2026, 10, 7, 9, 30, 0, DateTimeKind.Utc);

    private const long StorageAlertProductId = 100;
    private const long MachineOnlyAlertProductId = 200;

    /// <summary>
    /// The release-gate criterion, for both unusable statuses: the fleet section says why it is
    /// missing with the one stable code, and the ordering section is a real count that states it was
    /// taken over storage alone.
    /// </summary>
    [Theory]
    [InlineData(NayaxConnectionStatus.NotConfigured)]
    [InlineData(NayaxConnectionStatus.NeedsAttention)]
    public async Task A_business_that_is_not_usable_is_told_why_its_refill_card_is_missing(
        NayaxConnectionStatus status)
    {
        var summary = Assert.IsType<DashboardSummaryDto>(
            await UseCase(FleetRefusing(new NayaxNotConnectedException(status))).Handle(CancellationToken.None));

        var needsRefill = Assert.IsType<UnavailableDashboardSectionDto>(summary.NeedsRefill);
        Assert.False(needsRefill.Available);
        Assert.Equal("NayaxNotConnected", needsRefill.Reason);
        Assert.Equal("StorageOnly", summary.NeedsOrdering.Mode);
    }

    /// <summary>
    /// Issue #967: a business whose plan does not include the sales provider is told the same thing
    /// under its own code - the refill card is withheld rather than counted over a fleet nobody
    /// read, and the local ordering count is still a real figure.
    /// </summary>
    [Fact]
    public async Task A_business_whose_plan_excludes_the_provider_is_told_the_plan_is_why()
    {
        var summary = Assert.IsType<DashboardSummaryDto>(
            await UseCase(FleetRefusing(NayaxNotConnectedException.PlanRequired(SubscriptionPlan.Pro)))
                .Handle(CancellationToken.None));

        var needsRefill = Assert.IsType<UnavailableDashboardSectionDto>(summary.NeedsRefill);
        Assert.False(needsRefill.Available);
        Assert.Equal("PlanRequired", needsRefill.Reason);
        Assert.Equal("StorageOnly", summary.NeedsOrdering.Mode);
        // The local sections are untouched, which is the criterion "Free keeps everything else
        // unchanged": the week's sales and the inventory card are reads of this business's own data.
        Assert.NotNull(summary.SalesThisWeek);
        Assert.NotNull(summary.Inventory);
    }

    /// <summary>
    /// Availability is per feature (#329 P9): a connection that is perfectly usable, with one
    /// feature's reads Nayax has not granted, is reported as that feature being unavailable - not as
    /// a connection an operator should go and reconnect.
    /// </summary>
    [Fact]
    public async Task A_feature_nayax_has_not_granted_is_reported_as_its_own_reason()
    {
        var summary = Assert.IsType<DashboardSummaryDto>(
            await UseCase(FleetRefusing(new NayaxPermissionNotGrantedException("GetMachinesAsync")))
                .Handle(CancellationToken.None));

        Assert.Equal(
            "PermissionNotGranted",
            Assert.IsType<UnavailableDashboardSectionDto>(summary.NeedsRefill).Reason);
        Assert.Equal("StorageOnly", summary.NeedsOrdering.Mode);
    }

    /// <summary>
    /// The business outcome: everything that is local survives. The week's revenue, the inventory
    /// card and the business instant are value-for-value the ones the same request produces for a
    /// business whose fleet answered - a missing provider degrades one section, not the page.
    /// </summary>
    [Fact]
    public async Task Every_local_section_is_the_one_a_connected_business_is_given()
    {
        var withoutProvider = Assert.IsType<DashboardSummaryDto>(
            await UseCase(FleetRefusing(new NayaxNotConnectedException(NayaxConnectionStatus.NotConfigured)))
                .Handle(CancellationToken.None));
        var withProvider = Assert.IsType<DashboardSummaryDto>(
            await UseCase(EmptyFleet()).Handle(CancellationToken.None));

        Assert.Equal(withProvider.AsOfUtc, withoutProvider.AsOfUtc);
        Assert.Equal(withProvider.BusinessDate, withoutProvider.BusinessDate);
        Assert.Equal(withProvider.SalesThisWeek, withoutProvider.SalesThisWeek);
        Assert.Equal(withProvider.Inventory, withoutProvider.Inventory);
        // The fixtures really carry figures, so the comparison above is not two empty cards
        // agreeing with each other.
        Assert.Equal(1234.56m, withoutProvider.SalesThisWeek.Sales);
        Assert.Equal(1000m, withoutProvider.Inventory.InventoryValueAtCost);
        Assert.True(withoutProvider.Inventory.IsInventoryValueComplete);
    }

    /// <summary>
    /// What storage-only actually counts: the machines' replenishment need is 0, not missing. A
    /// product whose storage stock is at its threshold is still ordered, and a product that alerts
    /// only because machines are asking for it is not - counting it would need the fleet read this
    /// business cannot make.
    /// </summary>
    [Fact]
    public async Task The_storage_only_ordering_count_uses_a_machine_need_of_zero()
    {
        var withoutProvider = Assert.IsType<DashboardSummaryDto>(
            await UseCase(FleetRefusing(new NayaxNotConnectedException(NayaxConnectionStatus.NotConfigured)))
                .Handle(CancellationToken.None));

        Assert.Equal(1, withoutProvider.NeedsOrdering.ProductsNeedingOrdering);
        Assert.Equal(2, withoutProvider.NeedsOrdering.ProductsEvaluated);
    }

    /// <summary>
    /// The other side of the same catalogue: with the fleet read made, the machines' need is
    /// accounted for and the product that only they are short of is ordered too. It is the control
    /// for the test above - the two counts differ because of the mode and nothing else.
    /// </summary>
    [Fact]
    public async Task The_machine_aware_ordering_count_accounts_for_the_machines_need()
    {
        var fleet = FleetOf(55);
        fleet.Setup(client => client.GetMachineProductsAsync(55, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new NayaxMachineProduct
            {
                // Empty: PAR - MissingStockByMDB is 0, and the machines are asking for more units
                // than this product has in storage, which is what makes it a reorder alert only
                // once the fleet has been read.
                NayaxProductID = MachineOnlyAlertProductId, PAR = 40, MissingStockByMDB = 40, VendOutAlertThreshold = 2,
            }]);

        var summary = Assert.IsType<DashboardSummaryDto>(await UseCase(fleet).Handle(CancellationToken.None));

        Assert.Equal("MachineAware", summary.NeedsOrdering.Mode);
        Assert.Equal(2, summary.NeedsOrdering.ProductsNeedingOrdering);
        var needsRefill = Assert.IsType<DashboardRefillSummaryDto>(summary.NeedsRefill);
        Assert.Equal(1, needsRefill.MachinesNeedingRefill);
        Assert.Equal(1, needsRefill.MachinesEvaluated);
    }

    /// <summary>
    /// A caller whose role does not grant <c>Dashboard.ViewFinancials</c> (issue #527) is degraded
    /// the same way. The two decisions are independent: what the role withholds is money, and what
    /// the missing provider withholds is the machines.
    /// </summary>
    [Fact]
    public async Task An_operators_summary_degrades_the_same_section_the_same_way()
    {
        var summary = await UseCase(
                FleetRefusing(new NayaxNotConnectedException(NayaxConnectionStatus.NotConfigured)),
                role: BusinessRole.Operator)
            .Handle(CancellationToken.None);

        var redacted = Assert.IsType<RedactedDashboardSummaryDto>(summary);
        Assert.Equal(
            "NayaxNotConnected",
            Assert.IsType<UnavailableDashboardSectionDto>(redacted.NeedsRefill).Reason);
        Assert.Equal("StorageOnly", redacted.NeedsOrdering.Mode);
        Assert.Equal(1, redacted.NeedsOrdering.ProductsNeedingOrdering);
    }

    /// <summary>
    /// The regression guard for the existing contract: a business whose fleet answers is unchanged,
    /// refill card and all, and its ordering count says it accounted for the machines.
    /// </summary>
    [Fact]
    public async Task A_connected_business_still_receives_the_refill_card_itself()
    {
        var summary = Assert.IsType<DashboardSummaryDto>(
            await UseCase(EmptyFleet()).Handle(CancellationToken.None));

        Assert.IsType<DashboardRefillSummaryDto>(summary.NeedsRefill);
        Assert.Equal("MachineAware", summary.NeedsOrdering.Mode);
    }

    private static GetDashboardSummary UseCase(
        Mock<INayaxLynxClient> fleet, BusinessRole role = BusinessRole.Owner)
    {
        var outstandingOrders = new Mock<IOutstandingSupplierOrderQuantityStore>();
        outstandingOrders.Setup(store => store.GetOutstandingQuantitiesByProductAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<long, decimal>());

        var scope = new BusinessScope();
        scope.Resolve(BusinessId.From(1));

        return new GetDashboardSummary(
            new FakeClock(NowUtc),
            new FakeBusinessCalendar(NowUtc),
            new CurrentCapabilities(
                new FakeSupportSessionAccessor(),
                new FakeCurrentBusinessProvider(businessId: 1, role),
                new FakeRolePreviewAccessor()),
            scope,
            SalesFacts(),
            Catalog(),
            new CalculateReorderNeeds(
                fleet.Object, outstandingOrders.Object, FakeNayaxProductLinkStore.EveryIdLinkedToItself()));
    }

    private static FakeDashboardSummarySalesFactsProvider SalesFacts() =>
        new(currentPeriodSales: 1234.56m,
            currentPeriodTransactionCount: 7,
            priorPeriodSales: 1000m,
            priorPeriodTransactionCount: 5,
            earliestRecordedSaleUtc: NowUtc.AddYears(-1));

    /// <summary>
    /// Two products, so storage-only and machine-aware have different right answers over one
    /// catalogue: one is at its low-stock threshold on storage stock alone, and one is comfortably
    /// stocked in storage and short only in the machines.
    /// </summary>
    private static FakeProductCatalogStore Catalog() =>
        new(
            FakeProductCatalogStore.Product(
                StorageAlertProductId, "Coke", quantityInStock: 5, lowStockThreshold: 5, restockTo: 24) with
            {
                InventoryValue = 777.77m,
            },
            FakeProductCatalogStore.Product(
                MachineOnlyAlertProductId, "Chips", quantityInStock: 40, lowStockThreshold: 5, restockTo: 24) with
            {
                InventoryValue = 222.23m,
            });

    private static Mock<INayaxLynxClient> EmptyFleet()
    {
        var fleet = new Mock<INayaxLynxClient>();
        fleet.Setup(client => client.GetMachinesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        return fleet;
    }

    private static Mock<INayaxLynxClient> FleetOf(params long[] machineIds)
    {
        var fleet = new Mock<INayaxLynxClient>();
        fleet.Setup(client => client.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. machineIds.Select(id => new NayaxMachine { MachineID = id })]);
        return fleet;
    }

    private static Mock<INayaxLynxClient> FleetRefusing(Exception refusal)
    {
        var fleet = new Mock<INayaxLynxClient>();
        fleet.Setup(client => client.GetMachinesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(refusal);
        return fleet;
    }
}
