using Inventory.Application.Access;
using Inventory.Application.Dashboard;
using Inventory.Application.Nayax;
using Inventory.Application.Reorder;
using Inventory.Application.Tenancy;
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
/// The Dashboard summary in a support view (issue #552).
///
/// <para>A support session resolves no Nayax credentials at all (issue #551), so the live fleet read
/// behind the refill and ordering cards cannot be made. What this file is about is that the summary
/// degrades rather than fails: those two sections are published as unavailable with their reason,
/// the local sections are the real ones, and <c>CalculateReorderNeeds</c> is not called - the fleet
/// read is not attempted and then swallowed, it never starts.</para>
///
/// <para>The decision is taken from the published <c>IBusinessScope</c>, which is the same state
/// <c>NayaxRequestCredentialProvider</c> refuses in, so a member's request is reached by exactly the
/// same code and is unchanged; the control test below is what pins that down. The serialized shape
/// is asserted through the real request pipeline in
/// <c>InventoryApi.Tests.Controllers.SupportSessionRequestApiTests</c>.</para>
/// </summary>
public class GetDashboardSummarySupportViewTests
{
    private const int SupportedBusinessId = 1;

    // A Wednesday, so week-to-date is a genuinely partial week.
    private static readonly DateTime NowUtc = new(2026, 10, 7, 9, 30, 0, DateTimeKind.Utc);

    /// <summary>
    /// The criterion: the two sections the fleet feeds are replaced by
    /// <c>{ available: false, reason: "SupportView" }</c>, and the shape says so by type rather than
    /// by a count a caller would have to interpret.
    /// </summary>
    [Fact]
    public async Task The_fleet_sections_are_reported_unavailable_with_their_reason()
    {
        var summary = await UseCase(SupportScope()).Handle(CancellationToken.None);

        var supportView = Assert.IsType<SupportViewDashboardSummaryDto>(summary);
        Assert.False(supportView.NeedsRefill.Available);
        Assert.False(supportView.NeedsOrdering.Available);
        Assert.Equal("SupportView", supportView.NeedsRefill.Reason);
        Assert.Equal("SupportView", supportView.NeedsOrdering.Reason);
    }

    /// <summary>
    /// The criterion that is about work rather than output: no fleet read is made at all, so no
    /// Nayax credential is resolved, nothing is decrypted and nothing leaves the API. A section
    /// counted from a failed call and reported as unavailable would pass the test above and fail
    /// this one.
    /// </summary>
    [Fact]
    public async Task Not_one_nayax_call_is_attempted()
    {
        var fleet = EmptyFleet();

        _ = await UseCase(SupportScope(), fleet).Handle(CancellationToken.None);

        fleet.Verify(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()), Times.Never);
        fleet.Verify(
            x => x.GetMachineProductsAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// The other half of the business outcome: the summary still shows the business's local
    /// information. The week's revenue, its comparison and the whole inventory card are the figures
    /// a member of that business is given over the same fixtures - degrading two sections is not a
    /// reason to withhold the rest.
    /// </summary>
    [Fact]
    public async Task The_local_sections_are_the_ones_a_member_is_given()
    {
        var supportView = Assert.IsType<SupportViewDashboardSummaryDto>(
            await UseCase(SupportScope()).Handle(CancellationToken.None));
        var asMember = Assert.IsType<DashboardSummaryDto>(
            await UseCase(MemberScope()).Handle(CancellationToken.None));

        Assert.Equal(asMember.AsOfUtc, supportView.AsOfUtc);
        Assert.Equal(asMember.BusinessDate, supportView.BusinessDate);
        Assert.Equal(asMember.SalesThisWeek, supportView.SalesThisWeek);
        Assert.Equal(asMember.Inventory, supportView.Inventory);
        // The fixtures really do carry figures, so the comparison above is not two empty cards
        // agreeing with each other.
        Assert.Equal(1234.56m, supportView.SalesThisWeek.Sales);
        Assert.Equal(777.77m, supportView.Inventory.InventoryValueAtCost);
    }

    /// <summary>
    /// The regression guard: a member's own request is the full summary, computed from a fleet read
    /// that really is made. The degradation is a property of the support scope and of nothing else -
    /// not of the endpoint, and not of a Nayax connection that happens to be unusable, which is
    /// issue #329's separate case.
    /// </summary>
    [Fact]
    public async Task A_members_own_request_still_reads_the_fleet_and_keeps_its_shape()
    {
        var fleet = EmptyFleet();

        var summary = await UseCase(MemberScope(), fleet).Handle(CancellationToken.None);

        var full = Assert.IsType<DashboardSummaryDto>(summary);
        Assert.Equal(0, Assert.IsType<DashboardRefillSummaryDto>(full.NeedsRefill).MachinesEvaluated);
        Assert.Equal(1, full.NeedsOrdering.ProductsEvaluated);
        fleet.Verify(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// A denied scope - an unresolved membership - is not a support view, and is refused before
    /// anything is read. The support branch is reached by the one state that names a business
    /// read-only, never by "not a resolved member".
    /// </summary>
    [Fact]
    public async Task A_denied_scope_is_refused_rather_than_degraded()
    {
        var salesFacts = SalesFacts();
        var catalog = Catalog();

        await Assert.ThrowsAsync<BusinessAccessDeniedException>(
            () => UseCase(
                    new BusinessScope(),
                    capabilities: new CurrentCapabilities(
                        new FakeSupportSessionAccessor(),
                        FakeCurrentBusinessProvider.Denied(),
                        new FakeRolePreviewAccessor()),
                    salesFacts: salesFacts,
                    catalog: catalog)
                .Handle(CancellationToken.None));

        Assert.Equal(0, salesFacts.Calls);
        Assert.Equal(0, catalog.ListUnorderedCalls);
    }

    private static GetDashboardSummary UseCase(
        IBusinessScope businessScope,
        Mock<INayaxLynxClient>? fleet = null,
        CurrentCapabilities? capabilities = null,
        FakeDashboardSummarySalesFactsProvider? salesFacts = null,
        FakeProductCatalogStore? catalog = null)
    {
        var outstandingOrders = new Mock<IOutstandingSupplierOrderQuantityStore>();
        outstandingOrders.Setup(x => x.GetOutstandingQuantitiesByProductAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<long, decimal>());

        return new GetDashboardSummary(
            new FakeClock(NowUtc),
            new FakeBusinessCalendar(NowUtc),
            capabilities ?? Capabilities(businessScope),
            businessScope,
            salesFacts ?? SalesFacts(),
            catalog ?? Catalog(),
            new CalculateReorderNeeds(
                (fleet ?? EmptyFleet()).Object,
                outstandingOrders.Object,
                FakeNayaxProductLinkStore.EveryIdLinkedToItself()));
    }

    /// <summary>
    /// The capability source each scope really has: a support session's fixed set, which grants
    /// <c>Dashboard.ViewFinancials</c> and no role, or an Owner's role. Both come from
    /// <c>CurrentCapabilities</c>, the one place that chooses between them.
    /// </summary>
    private static CurrentCapabilities Capabilities(IBusinessScope businessScope) =>
        businessScope.State == BusinessScopeState.SupportReadOnly
            ? new CurrentCapabilities(
                FakeSupportSessionAccessor.Active(SupportedBusinessId),
                FakeCurrentBusinessProvider.SupportSession(SupportedBusinessId),
                new FakeRolePreviewAccessor())
            : new CurrentCapabilities(
                new FakeSupportSessionAccessor(),
                new FakeCurrentBusinessProvider(SupportedBusinessId),
                new FakeRolePreviewAccessor());

    private static Mock<INayaxLynxClient> EmptyFleet()
    {
        var fleet = new Mock<INayaxLynxClient>();
        fleet.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        return fleet;
    }

    private static FakeDashboardSummarySalesFactsProvider SalesFacts() =>
        new(currentPeriodSales: 1234.56m,
            currentPeriodTransactionCount: 7,
            priorPeriodSales: 1000m,
            priorPeriodTransactionCount: 5,
            earliestRecordedSaleUtc: NowUtc.AddYears(-1));

    private static FakeProductCatalogStore Catalog() =>
        new(FakeProductCatalogStore.Product(
                100, "Coke", quantityInStock: 12, lowStockThreshold: 5, restockTo: 24) with
        {
            InventoryValue = 777.77m,
        });

    private static BusinessScope SupportScope()
    {
        var scope = new BusinessScope();
        scope.ResolveForSupportSession(BusinessId.From(SupportedBusinessId));
        return scope;
    }

    private static BusinessScope MemberScope()
    {
        var scope = new BusinessScope();
        scope.Resolve(BusinessId.From(SupportedBusinessId));
        return scope;
    }
}
