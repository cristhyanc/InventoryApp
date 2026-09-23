#nullable enable
using System;
using System.Linq;
using InventoryApi.Controllers;
using InventoryApi.Tests.Swagger;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// Locks the HTTP surface of the Purchase business record across the internal
/// Receipt-to-Purchase rename (issue #60). <see cref="PurchasesController"/> would otherwise
/// derive "api/purchases" from its own type name; these tests assert the effective routes the
/// MVC API explorer actually resolves, not the presence of an attribute.
/// </summary>
public class PurchasesControllerRouteTests
{
    private const string LegacyBaseRoute = "api/receipts";

    [Fact]
    public void Every_purchase_action_is_served_under_the_legacy_api_receipts_base_route()
    {
        var descriptions = ApiContractTestHost.GetApiDescriptionsFor<PurchasesController>();

        Assert.NotEmpty(descriptions);
        Assert.All(descriptions, description =>
            Assert.True(
                description.RelativePath == LegacyBaseRoute ||
                description.RelativePath!.StartsWith(LegacyBaseRoute + "/", StringComparison.Ordinal),
                $"'{description.RelativePath}' is not under '{LegacyBaseRoute}'."));
    }

    [Fact]
    public void No_route_in_the_application_was_renamed_to_api_purchases()
    {
        var purchaseRoutes = ApiContractTestHost.GetApiDescriptions()
            .Where(description => description.RelativePath is not null &&
                description.RelativePath.StartsWith("api/purchases", StringComparison.OrdinalIgnoreCase))
            .Select(description => description.RelativePath)
            .ToList();

        Assert.Empty(purchaseRoutes);
    }

    [Theory]
    [InlineData("GET", "api/receipts")]
    [InlineData("POST", "api/receipts")]
    [InlineData("GET", "api/receipts/{id}")]
    [InlineData("PUT", "api/receipts/{id}")]
    [InlineData("DELETE", "api/receipts/{id}")]
    [InlineData("GET", "api/receipts/{id}/file")]
    public void Legacy_purchase_endpoint_is_still_available(string httpMethod, string relativePath)
    {
        var descriptions = ApiContractTestHost.GetApiDescriptionsFor<PurchasesController>();

        Assert.Contains(descriptions, description =>
            string.Equals(description.HttpMethod, httpMethod, StringComparison.OrdinalIgnoreCase) &&
            description.RelativePath == relativePath);
    }
}
