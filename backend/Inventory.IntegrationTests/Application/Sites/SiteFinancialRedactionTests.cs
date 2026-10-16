using Inventory.Application.Access;
using Inventory.Application.Nayax;
using Inventory.Application.Sites;
using Inventory.Application.Tenancy;
using Inventory.Domain.Sites;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Sites;
using InventoryApi.Tests.Application.Access;
using InventoryApi.Tests.Application.Gst;
using InventoryApi.Tests.Application.SupportSessions;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.Sites;

/// <summary>
/// Which site figures each use case calculates for whom (issue #697).
///
/// <para>Sites is part of the day-to-day inventory work, so every role holds
/// <c>Inventory.Operate</c> and reaches both use cases; the three revenue figures and the estimated
/// card profit additionally need <c>Dashboard.ViewFinancials</c>. The expected roles are written out
/// literally from the agreed table of issue #328 rather than read back from
/// <c>RoleCapabilities</c> - a test that asked the capability table what it allows would pass
/// whatever that table happened to say.</para>
///
/// <para>These cases are about the decision and the reads it avoids. The exact raw JSON an Operator,
/// a Manager and an Owner receive through the real request pipeline is
/// <c>InventoryApi.Tests.Controllers.SiteFinancialRedactionApiTests</c>.</para>
/// </summary>
public class SiteFinancialRedactionTests
{
    private const long SiteId = 42;
    private const long MachineId = 10;
    private const long ProductId = 1;

    /// <summary>
    /// 14:30 UTC on Wednesday 11 March 2026, which is 01:30 on Thursday 12 March in Sydney - the
    /// same fixed instant <c>GetSiteSummariesTests</c> pins its periods to.
    /// </summary>
    private static readonly FixedSydneyTime Time =
        new(new DateTime(2026, 3, 11, 14, 30, 0, DateTimeKind.Utc));

    private static readonly SiteProductActivityFact[] ActiveProducts = [new(ProductId, IsActive: true)];

    private static readonly Dictionary<long, SiteProductCostBasis> CostBasis = new()
    {
        [ProductId] = new SiteProductCostBasis("Chips", AverageUnitCost: 2m, HasCostBasis: true),
    };

    private static readonly Dictionary<decimal, decimal> CommissionByPrice = new() { [5m] = 0.5m };

    // --- GET /api/sites -----------------------------------------------------------------------

    /// <summary>
    /// An Operator holds <c>Inventory.Operate</c> and not <c>Dashboard.ViewFinancials</c>, so every
    /// revenue figure is withheld - <c>null</c>, never the <c>0</c> an empty period would report.
    /// </summary>
    [Fact]
    public async Task An_operators_site_summary_carries_no_revenue_figure()
    {
        var site = Assert.Single(await Summaries(BusinessRole.Operator, Facts()));

        Assert.Null(site.TodayRevenue);
        Assert.Null(site.CurrentWeekRevenue);
        Assert.Null(site.PreviousComparableWeekRevenue);
    }

    /// <summary>
    /// The acceptance criterion that is about work rather than output: the completed sales are not
    /// read for such a caller, so there is no sales query in the request and no figure in it to leak.
    /// </summary>
    [Fact]
    public async Task An_operators_site_revenue_is_not_calculated_at_all()
    {
        var facts = Facts();

        await Summaries(BusinessRole.Operator, facts);

        Assert.Equal(new[] { "start:activity", "end:activity" }, facts.Trace);
        Assert.Empty(facts.CompletedSalesRequests);
    }

    [Theory]
    [InlineData(BusinessRole.Manager)]
    [InlineData(BusinessRole.Owner)]
    public async Task A_role_that_holds_the_financial_capability_is_given_every_site_revenue_figure(
        BusinessRole role)
    {
        var facts = Facts();

        var site = Assert.Single(await Summaries(role, facts));

        Assert.Equal(9m, site.TodayRevenue);
        Assert.Equal(9m, site.CurrentWeekRevenue);
        Assert.Equal(3m, site.PreviousComparableWeekRevenue);
        Assert.Single(facts.CompletedSalesRequests);
    }

    /// <summary>
    /// Redaction removes the three amounts and changes nothing else: every other figure is the one
    /// the same request produces for an Owner over the same fleet and fixtures.
    /// </summary>
    [Fact]
    public async Task An_operators_site_summary_matches_an_owners_apart_from_the_revenue()
    {
        var asOperator = Assert.Single(await Summaries(BusinessRole.Operator, Facts()));
        var asOwner = Assert.Single(await Summaries(BusinessRole.Owner, Facts()));

        Assert.Equal(
            asOwner with { TodayRevenue = null, CurrentWeekRevenue = null, PreviousComparableWeekRevenue = null },
            asOperator);

        // The fixture really has stock to report, so the comparison above is not two empty rows
        // agreeing with each other.
        Assert.Equal(40m, asOperator.TotalStockPercentage);
    }

    /// <summary>
    /// A support session (issue #551) grants <c>Dashboard.ViewFinancials</c>, so the decision is
    /// taken from that fixed set rather than from a role the platform administrator does not hold -
    /// and asking for a role, which would deny the request, is not what happens.
    /// </summary>
    [Fact]
    public async Task A_support_session_is_given_the_site_revenue_without_a_role()
    {
        var site = Assert.Single(await new GetSiteSummaries(
                Fleet(), Facts(), new SiteNameResolver(), Time.Clock, Time.Calendar, SupportSession())
            .Handle(CancellationToken.None));

        Assert.Equal(9m, site.TodayRevenue);
    }

    /// <summary>
    /// Fail closed: a caller whose membership does not resolve is refused before anything is read,
    /// rather than being treated as the least-privileged role. No request can reach this - the
    /// endpoint's <c>Inventory.Operate</c> policy refuses such a caller first - and it must stay
    /// refused if one ever could.
    /// </summary>
    [Fact]
    public async Task An_unresolved_membership_reads_no_site_fact_at_all()
    {
        var facts = Facts();

        await Assert.ThrowsAsync<BusinessAccessDeniedException>(
            () => new GetSiteSummaries(
                    Fleet(), facts, new SiteNameResolver(), Time.Clock, Time.Calendar, Denied())
                .Handle(CancellationToken.None));

        Assert.Empty(facts.Trace);
    }

    // --- GET /api/sites/{id}/products ---------------------------------------------------------

    /// <summary>
    /// The recorded decision in full: the estimate is withheld from an Operator while the unit cost
    /// and the site price stay visible, because those are what a role that stocks and prices the
    /// machines needs.
    /// </summary>
    [Fact]
    public async Task An_operators_site_product_carries_no_estimate_but_keeps_its_cost_and_price()
    {
        var product = Assert.Single(await Products(BusinessRole.Operator, Facts()));

        Assert.Null(product.EstimatedCardProfit);
        Assert.Equal(2m, product.AverageUnitCost);
        Assert.Equal(5m, product.SitePrice);
    }

    /// <summary>
    /// The commission and the Nayax fee configuration are read only to calculate the estimate, so
    /// for a caller who may not see it neither read is made; the cost basis still is, because the
    /// unit cost it carries is not withheld.
    /// </summary>
    [Fact]
    public async Task An_operators_estimate_is_not_calculated_at_all()
    {
        var facts = Facts();

        await Products(BusinessRole.Operator, facts);

        Assert.Equal(new[] { "start:costBasis", "end:costBasis" }, facts.Trace);
    }

    [Theory]
    [InlineData(BusinessRole.Manager)]
    [InlineData(BusinessRole.Owner)]
    public async Task A_role_that_holds_the_financial_capability_is_given_the_estimate(BusinessRole role)
    {
        var facts = Facts();

        var product = Assert.Single(await Products(role, facts));

        // 5.00 retail - 0.50 commission - 2.00 cost - 0.22 fee including GST.
        Assert.Equal(2.28m, product.EstimatedCardProfit);
        Assert.Equal(
            new[]
            {
                "start:costBasis", "end:costBasis", "start:commission", "end:commission",
                "start:fee", "end:fee",
            },
            facts.Trace);
    }

    /// <summary>
    /// Redaction removes one amount and changes nothing else. The row is compared field by field
    /// rather than as a record, because <see cref="SiteProductRecord.MachineMdbCodes"/> is a list and
    /// record equality would compare the two calls' lists by reference.
    /// </summary>
    [Fact]
    public async Task An_operators_site_product_matches_an_owners_apart_from_the_estimate()
    {
        var asOperator = Assert.Single(await Products(BusinessRole.Operator, Facts()));
        var asOwner = Assert.Single(await Products(BusinessRole.Owner, Facts()));

        Assert.Null(asOperator.EstimatedCardProfit);
        Assert.Equal(2.28m, asOwner.EstimatedCardProfit);
        Assert.Equal(asOwner.ProductId, asOperator.ProductId);
        Assert.Equal(asOwner.Name, asOperator.Name);
        Assert.Equal(asOwner.AverageUnitCost, asOperator.AverageUnitCost);
        Assert.Equal(asOwner.SitePrice, asOperator.SitePrice);
        Assert.Equal(asOwner.QuantityInStock, asOperator.QuantityInStock);
        Assert.Equal(asOwner.MaxStock, asOperator.MaxStock);
        Assert.Equal(asOwner.MdbCode, asOperator.MdbCode);
        Assert.Equal(asOwner.MachineMdbCodes, asOperator.MachineMdbCodes);

        // The fixture really has stock and an MDB code to report.
        Assert.Equal(4, asOperator.QuantityInStock);
        Assert.Equal(3, asOperator.MdbCode);
    }

    [Fact]
    public async Task A_support_session_is_given_the_estimate_without_a_role()
    {
        var product = Assert.Single(
            await new GetSiteProducts(Fleet(), Facts(), Time.Calendar, SupportSession())
                .Handle(SiteId, CancellationToken.None));

        Assert.Equal(2.28m, product.EstimatedCardProfit);
    }

    [Fact]
    public async Task An_unresolved_membership_reads_no_site_product_fact_at_all()
    {
        var facts = Facts();

        await Assert.ThrowsAsync<BusinessAccessDeniedException>(
            () => new GetSiteProducts(Fleet(), facts, Time.Calendar, Denied())
                .Handle(SiteId, CancellationToken.None));

        Assert.Empty(facts.Trace);
    }

    private static Task<IReadOnlyList<SiteSummary>> Summaries(BusinessRole role, ISiteFactsStore facts) =>
        new GetSiteSummaries(
                Fleet(), facts, new SiteNameResolver(), Time.Clock, Time.Calendar,
                TestCurrentCapabilities.ForRole(role))
            .Handle(CancellationToken.None);

    private static Task<IReadOnlyList<SiteProductRecord>> Products(BusinessRole role, ISiteFactsStore facts) =>
        new GetSiteProducts(Fleet(), facts, Time.Calendar, TestCurrentCapabilities.ForRole(role))
            .Handle(SiteId, CancellationToken.None);

    /// <summary>A request inside an active support session for the same business.</summary>
    private static CurrentCapabilities SupportSession() =>
        new(
            FakeSupportSessionAccessor.Active(businessId: 1),
            FakeCurrentBusinessProvider.SupportSession(1),
            new FakeRolePreviewAccessor());

    /// <summary>A caller whose membership does not resolve, for any denial reason.</summary>
    private static CurrentCapabilities Denied() =>
        new(
            new FakeSupportSessionAccessor(),
            FakeCurrentBusinessProvider.Denied(),
            new FakeRolePreviewAccessor());

    /// <summary>
    /// Everything both use cases read, so one fixture serves the revenue and the estimate: an active
    /// catalogue product with a cost basis, a commission amount and a fee rate, and completed sales
    /// placed in the periods the fixed clock resolves.
    /// </summary>
    private static RecordingSiteFactsStore Facts() =>
        new(
            productActivity: ActiveProducts,
            sales:
            [
                new(MachineId, SettlementValue: 9m, MachineAuthorizationTime: Time.NowUtc),
                new(MachineId, SettlementValue: 3m,
                    MachineAuthorizationTime: Time.Window.PreviousComparableWeek.StartUtc),
            ],
            costBasis: CostBasis,
            commissionByPrice: CommissionByPrice,
            feeExGst: 0.20m);

    /// <summary>One machine at the site, holding one priced, partly missing selection.</summary>
    private static RecordingNayaxLynxClient Fleet() =>
        new(
            [new NayaxMachine { MachineID = MachineId, CustomerID = SiteId, MachineName = "Lobby" }],
            new Dictionary<long, List<NayaxMachineProduct>>
            {
                [MachineId] =
                [
                    new()
                    {
                        MachineID = MachineId, NayaxProductID = ProductId, RetailPrice = 5m, PAR = 10,
                        MissingStockByMDB = 6, VendOutAlertThreshold = 1, MDBCode = 3,
                    },
                ],
            });
}
