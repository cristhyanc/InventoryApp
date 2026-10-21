using System.Reflection;
using Inventory.Application.Access;
using InventoryApi.Auth;
using InventoryApi.Controllers;
using InventoryApi.Tests.Swagger;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The published HTTP surface of the bulk product purchase-cost summary (issue #788, B of #786): one
/// route, one method, and the capability it declares.
///
/// <para>The capability is the part worth pinning, because it is a security decision rather than a
/// shape. The product catalogue is <c>Inventory.Operate</c> - an Operator refills machines from it -
/// while this endpoint reports what every product was bought for and from whom, which issue #788
/// places behind <c>Dashboard.ViewFinancials</c>. A later change that moved this action onto
/// <c>ProductsController</c> would inherit the catalogue's capability and hand those supplier
/// purchasing facts to every role that may operate inventory, which no diff should be able to do
/// quietly. The refusal direction itself is asserted over the real pipeline, against the agreed role
/// matrix, in <c>InventoryApi.Tests.Auth.CapabilityAuthorizationApiTests</c>.</para>
///
/// <para>One route and no more: a second action here would be another read of financial purchasing
/// data, which is a reviewed decision rather than an implementation detail. The behaviour behind the
/// route is <c>ProductPurchaseCostSummaryApiTests</c> over a relational database.</para>
/// </summary>
public class ProductPurchaseCostsApiContractTests
{
    [Fact]
    public void The_controller_publishes_exactly_one_route()
    {
        Assert.Equal(
            [("GET", "api/products/purchase-cost-summary")],
            ApiContractTestHost.GetApiDescriptionsFor<ProductPurchaseCostsController>()
                .Select(description => (
                    description.HttpMethod?.ToUpperInvariant() ?? string.Empty,
                    description.RelativePath ?? string.Empty))
                .OrderBy(route => route.Item2, StringComparer.Ordinal)
                .ToList());
    }

    /// <summary>
    /// The capability is declared on the controller, so every action it ever holds requires it:
    /// a new action added here cannot be reachable by a role this one refuses.
    /// </summary>
    [Fact]
    public void The_controller_requires_the_financial_capability() =>
        Assert.Equal(
            [nameof(Capability.DashboardViewFinancials)],
            typeof(ProductPurchaseCostsController)
                .GetCustomAttributes<RequireCapabilityAttribute>()
                .Select(attribute => attribute.Capability.ToString())
                .ToList());
}
