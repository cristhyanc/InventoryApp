#nullable enable
using InventoryApi.Controllers;
using InventoryApi.Tests.Swagger;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// Locks the canonical HTTP surface of the OperatingExpense attachment endpoint (issue #61):
/// <see cref="OperatingExpensesController.GetAttachment"/> is served only under
/// "api/operating-expenses/{id}/attachment", with no remaining "/receipt" alias. These tests
/// assert the effective routes the MVC API explorer actually resolves, not the presence of an
/// attribute.
/// </summary>
public class OperatingExpensesControllerRouteTests
{
    [Fact]
    public void Canonical_attachment_endpoint_is_available()
    {
        var descriptions = ApiContractTestHost.GetApiDescriptionsFor<OperatingExpensesController>();

        Assert.Contains(descriptions, description =>
            string.Equals(description.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase) &&
            description.RelativePath == "api/operating-expenses/{id}/attachment");
    }

    [Fact]
    public void No_route_in_the_application_still_uses_the_legacy_receipt_alias()
    {
        var receiptRoutes = ApiContractTestHost.GetApiDescriptions()
            .Where(description => description.RelativePath is not null &&
                description.RelativePath.EndsWith("/receipt", StringComparison.OrdinalIgnoreCase))
            .Select(description => description.RelativePath)
            .ToList();

        Assert.Empty(receiptRoutes);
    }
}
