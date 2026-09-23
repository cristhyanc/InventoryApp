#nullable enable
using InventoryApi.Controllers;
using InventoryApi.Tests.Swagger;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// Locks the canonical HTTP surface of the Purchase business record (issue #127):
/// <see cref="PurchasesController"/> is served under "api/purchases" end to end, with no
/// remaining "api/receipts" route. These tests assert the effective routes the MVC API
/// explorer actually resolves, not the presence of an attribute.
/// </summary>
public class PurchasesControllerRouteTests
{
    private const string CanonicalBaseRoute = "api/purchases";

    [Fact]
    public void Every_purchase_action_is_served_under_the_canonical_api_purchases_base_route()
    {
        var descriptions = ApiContractTestHost.GetApiDescriptionsFor<PurchasesController>();

        Assert.NotEmpty(descriptions);
        Assert.All(descriptions, description =>
            Assert.True(
                description.RelativePath == CanonicalBaseRoute ||
                description.RelativePath!.StartsWith(CanonicalBaseRoute + "/", StringComparison.Ordinal),
                $"'{description.RelativePath}' is not under '{CanonicalBaseRoute}'."));
    }

    [Fact]
    public void No_route_in_the_application_still_uses_api_receipts()
    {
        var receiptRoutes = ApiContractTestHost.GetApiDescriptions()
            .Where(description => description.RelativePath is not null &&
                description.RelativePath.StartsWith("api/receipts", StringComparison.OrdinalIgnoreCase))
            .Select(description => description.RelativePath)
            .ToList();

        Assert.Empty(receiptRoutes);
    }

    [Theory]
    [InlineData("GET", "api/purchases")]
    [InlineData("POST", "api/purchases")]
    [InlineData("GET", "api/purchases/{id}")]
    [InlineData("PUT", "api/purchases/{id}")]
    [InlineData("DELETE", "api/purchases/{id}")]
    [InlineData("GET", "api/purchases/{id}/file")]
    public void Canonical_purchase_endpoint_is_available(string httpMethod, string relativePath)
    {
        var descriptions = ApiContractTestHost.GetApiDescriptionsFor<PurchasesController>();

        Assert.Contains(descriptions, description =>
            string.Equals(description.HttpMethod, httpMethod, StringComparison.OrdinalIgnoreCase) &&
            description.RelativePath == relativePath);
    }
}
