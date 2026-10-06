#nullable enable
using InventoryApi.Controllers;
using InventoryApi.Tests.Swagger;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// Pins the effective HTTP surface of <see cref="StockController"/> through the MVC API explorer,
/// so issue #305's move to API-owned response DTOs cannot quietly add, drop or re-template a route.
/// The restock-cost-suggestion endpoint is named explicitly because it is the one route that is not
/// a verb on the collection and is the easiest to lose.
/// </summary>
public class StockControllerRouteTests
{
    [Fact]
    public void Stock_endpoints_keep_their_exact_routes_and_methods()
    {
        var routes = ApiContractTestHost.GetApiDescriptionsFor<StockController>()
            .Select(description => $"{description.HttpMethod?.ToUpperInvariant()} {description.RelativePath}")
            .OrderBy(route => route, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "GET api/products/{productId}/stock",
                "GET api/products/{productId}/stock/restock-cost-suggestion",
                "POST api/products/{productId}/stock",
            },
            routes);
    }
}
