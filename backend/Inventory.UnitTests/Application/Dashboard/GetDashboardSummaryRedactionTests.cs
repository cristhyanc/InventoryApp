using Inventory.Application.Access;
using Inventory.Application.Dashboard;
using Inventory.Application.Nayax;
using Inventory.Application.Products;
using Inventory.Application.Reorder;
using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Gst;
using InventoryApi.Tests.Application.Products;
using InventoryApi.Tests.Application.SupportSessions;
using InventoryApi.Tests.Application.Time;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.Dashboard;

/// <summary>
/// Which figures the Dashboard summary calculates for whom (issue #527).
///
/// <para>The Dashboard is the landing page, so every role reaches this use case; the week's revenue
/// and the inventory valuation additionally need <c>Dashboard.ViewFinancials</c>. The expected roles
/// are written out literally from the agreed table of issue #328 rather than read back from
/// <c>RoleCapabilities</c> - a test that asked the capability table what it allows would pass
/// whatever that table happened to say.</para>
///
/// <para>The exact serialized property sets are asserted through the real request pipeline in
/// <c>InventoryApi.Tests.Controllers.DashboardSummaryRedactionApiTests</c>. What these tests are
/// about is the decision and the reads it avoids.</para>
/// </summary>
public class GetDashboardSummaryRedactionTests
{
    // A Wednesday, so week-to-date is a genuinely partial week.
    private static readonly DateTime NowUtc = new(2026, 10, 7, 9, 30, 0, DateTimeKind.Utc);

    private static GetDashboardSummary UseCase(
        ICurrentBusinessProvider currentBusiness,
        IDashboardSummarySalesFactsProvider salesFacts,
        IProductCatalogStore catalog,
        Mock<INayaxLynxClient>? nayax = null) =>
        UseCase(
            new CurrentCapabilities(new FakeSupportSessionAccessor(), currentBusiness),
            salesFacts,
            catalog,
            nayax);

    private static GetDashboardSummary UseCase(
        CurrentCapabilities capabilities,
        IDashboardSummarySalesFactsProvider salesFacts,
        IProductCatalogStore catalog,
        Mock<INayaxLynxClient>? nayax = null,
        IBusinessScope? businessScope = null)
    {
        var fleet = nayax ?? EmptyFleet();
        var outstandingOrders = new Mock<IOutstandingSupplierOrderQuantityStore>();
        outstandingOrders.Setup(x => x.GetOutstandingQuantitiesByProductAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<long, decimal>());

        return new GetDashboardSummary(
            new FakeClock(NowUtc),
            new FakeBusinessCalendar(NowUtc),
            capabilities,
            businessScope ?? MemberScope(),
            salesFacts,
            catalog,
            new CalculateReorderNeeds(fleet.Object, outstandingOrders.Object));
    }

    /// <summary>
    /// An ordinary request's resolved scope: these tests are about which figures a role is given,
    /// so none of them is a support view. The support view's own shape (issue #552) is
    /// <c>GetDashboardSummarySupportViewTests</c>.
    /// </summary>
    private static BusinessScope MemberScope()
    {
        var scope = new BusinessScope();
        scope.Resolve(BusinessId.From(1));
        return scope;
    }

    private static BusinessScope SupportScope()
    {
        var scope = new BusinessScope();
        scope.ResolveForSupportSession(BusinessId.From(1));
        return scope;
    }

    private static Mock<INayaxLynxClient> EmptyFleet()
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        return nayax;
    }

    private static FakeDashboardSummarySalesFactsProvider SalesFacts() =>
        new(currentPeriodSales: 1234.56m,
            currentPeriodTransactionCount: 7,
            priorPeriodSales: 1000m,
            priorPeriodTransactionCount: 5,
            earliestRecordedSaleUtc: NowUtc.AddYears(-1));

    private static FakeProductCatalogStore Catalog(decimal? inventoryValue = 777.77m) =>
        new(FakeProductCatalogStore.Product(
                100, "Coke", quantityInStock: 12, lowStockThreshold: 5, restockTo: 24) with
        {
            InventoryValue = inventoryValue,
        });

    /// <summary>
    /// A support session (issue #551) grants <c>Dashboard.ViewFinancials</c>, so the decision is
    /// taken from that set rather than from a role the platform administrator does not hold - and
    /// asking for a role, which would deny the request, is not what happens.
    ///
    /// <para>Since issue #552 such a request is answered the support view's own shape, and this is
    /// still the claim it makes about the figures: the week's revenue and the valuation are in it,
    /// decided from the session's capability set.</para>
    /// </summary>
    [Fact]
    public async Task A_support_session_is_answered_the_financial_figures_without_a_role()
    {
        var summary = await UseCase(
                new CurrentCapabilities(
                    FakeSupportSessionAccessor.Active(businessId: 1),
                    FakeCurrentBusinessProvider.SupportSession(businessId: 1)),
                SalesFacts(),
                Catalog(),
                businessScope: SupportScope())
            .Handle(CancellationToken.None);

        var supportView = Assert.IsType<SupportViewDashboardSummaryDto>(summary);
        Assert.Equal(1234.56m, supportView.SalesThisWeek.Sales);
        Assert.Equal(777.77m, supportView.Inventory.InventoryValueAtCost);
    }

    /// <summary>
    /// An Operator holds <c>Dashboard.View</c> and not <c>Dashboard.ViewFinancials</c>, so they are
    /// answered the shape that has no week's revenue and no valuation at all.
    /// </summary>
    [Fact]
    public async Task An_operator_is_answered_the_shape_without_the_financial_figures()
    {
        var summary = await UseCase(
                new FakeCurrentBusinessProvider(businessId: 1, BusinessRole.Operator),
                SalesFacts(),
                Catalog())
            .Handle(CancellationToken.None);

        var redacted = Assert.IsType<RedactedDashboardSummaryDto>(summary);
        Assert.Equal(NowUtc, redacted.AsOfUtc);
        Assert.Equal(NowUtc.Date, redacted.BusinessDate);
    }

    /// <summary>
    /// The acceptance criterion that is about work rather than output: the sales facts are not read
    /// for such a caller, so the week-to-date query never runs and there is no figure in the request
    /// to leak.
    /// </summary>
    [Fact]
    public async Task An_operators_sales_figures_are_not_calculated_at_all()
    {
        var salesFacts = SalesFacts();

        await UseCase(
                new FakeCurrentBusinessProvider(businessId: 1, BusinessRole.Operator),
                salesFacts,
                Catalog())
            .Handle(CancellationToken.None);

        Assert.Equal(0, salesFacts.Calls);
        Assert.Null(salesFacts.CurrentPeriod);
        Assert.Null(salesFacts.PriorPeriod);
    }

    /// <summary>
    /// Redaction removes two amounts and changes nothing else: every other figure is the one the
    /// same request produces for an Owner over the same fixtures.
    /// </summary>
    [Fact]
    public async Task An_operator_is_given_the_same_non_financial_figures_as_an_owner()
    {
        var asOperator = Assert.IsType<RedactedDashboardSummaryDto>(
            await UseCase(
                    new FakeCurrentBusinessProvider(businessId: 1, BusinessRole.Operator),
                    SalesFacts(),
                    Catalog())
                .Handle(CancellationToken.None));
        var asOwner = Assert.IsType<DashboardSummaryDto>(
            await UseCase(
                    new FakeCurrentBusinessProvider(businessId: 1, BusinessRole.Owner),
                    SalesFacts(),
                    Catalog())
                .Handle(CancellationToken.None));

        Assert.Equal(asOwner.AsOfUtc, asOperator.AsOfUtc);
        Assert.Equal(asOwner.BusinessDate, asOperator.BusinessDate);
        Assert.Equal(asOwner.NeedsRefill, asOperator.NeedsRefill);
        Assert.Equal(asOwner.NeedsOrdering, asOperator.NeedsOrdering);
        Assert.Equal(asOwner.Inventory.IsInventoryValueComplete, asOperator.Inventory.IsInventoryValueComplete);
        Assert.Equal(asOwner.Inventory.ProductsWithUnknownCost, asOperator.Inventory.ProductsWithUnknownCost);
        Assert.Equal(asOwner.Inventory.ProductCount, asOperator.Inventory.ProductCount);
        Assert.Equal(asOwner.Inventory.UnitsInStorage, asOperator.Inventory.UnitsInStorage);
    }

    /// <summary>
    /// The valuation's data-quality state survives redaction. A caller who may not see the amount is
    /// still told the catalogue's costing is incomplete and for how many products: that is a fact
    /// about their stock records, not a sum of money, and it is what keeps an incomplete period
    /// visibly incomplete for every role.
    /// </summary>
    [Fact]
    public async Task An_operator_is_still_told_the_valuation_is_incomplete()
    {
        var summary = await UseCase(
                new FakeCurrentBusinessProvider(businessId: 1, BusinessRole.Operator),
                SalesFacts(),
                Catalog(inventoryValue: null))
            .Handle(CancellationToken.None);

        var inventory = Assert.IsType<RedactedDashboardSummaryDto>(summary).Inventory;
        Assert.False(inventory.IsInventoryValueComplete);
        Assert.Equal(1, inventory.ProductsWithUnknownCost);
        Assert.Equal(1, inventory.ProductCount);
        Assert.Equal(12, inventory.UnitsInStorage);
    }

    /// <summary>
    /// The other direction, which is what makes the tests above a redaction claim rather than an
    /// endpoint that shows nobody anything: a Manager and an Owner hold
    /// <c>Dashboard.ViewFinancials</c> and are given the whole summary with its real figures, read
    /// once.
    /// </summary>
    [Theory]
    [InlineData(BusinessRole.Manager)]
    [InlineData(BusinessRole.Owner)]
    public async Task A_role_that_holds_the_financial_capability_is_given_every_figure(BusinessRole role)
    {
        var salesFacts = SalesFacts();

        var summary = await UseCase(
                new FakeCurrentBusinessProvider(businessId: 1, role), salesFacts, Catalog())
            .Handle(CancellationToken.None);

        var full = Assert.IsType<DashboardSummaryDto>(summary);
        Assert.Equal(1234.56m, full.SalesThisWeek.Sales);
        Assert.Equal(7, full.SalesThisWeek.TransactionCount);
        Assert.Equal(1000m, full.SalesThisWeek.ComparisonSales);
        Assert.Equal(777.77m, full.Inventory.InventoryValueAtCost);
        Assert.True(full.Inventory.IsInventoryValueComplete);
        Assert.Equal(1, salesFacts.Calls);
    }

    /// <summary>
    /// Fail closed: a caller whose membership does not resolve - for any reason - is refused, and
    /// nothing is read. The least-privileged role is not a fallback, and neither is the fullest one.
    /// </summary>
    [Theory]
    [InlineData(BusinessAccessDenialReason.NotAuthenticated)]
    [InlineData(BusinessAccessDenialReason.UnidentifiableActor)]
    [InlineData(BusinessAccessDenialReason.MembershipMissing)]
    [InlineData(BusinessAccessDenialReason.MembershipInactive)]
    [InlineData(BusinessAccessDenialReason.MembershipAmbiguous)]
    [InlineData(BusinessAccessDenialReason.BusinessInactive)]
    [InlineData(BusinessAccessDenialReason.RoleUnrecognised)]
    public async Task A_caller_whose_membership_does_not_resolve_is_refused_and_reads_nothing(
        BusinessAccessDenialReason reason)
    {
        var salesFacts = SalesFacts();
        var catalog = Catalog();
        var fleet = EmptyFleet();

        await Assert.ThrowsAsync<BusinessAccessDeniedException>(
            () => UseCase(FakeCurrentBusinessProvider.Denied(reason), salesFacts, catalog, fleet)
                .Handle(CancellationToken.None));

        Assert.Equal(0, salesFacts.Calls);
        Assert.Equal(0, catalog.ListUnorderedCalls);
        fleet.Verify(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
