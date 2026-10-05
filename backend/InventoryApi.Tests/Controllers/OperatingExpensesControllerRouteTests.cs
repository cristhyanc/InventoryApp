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

    /// <summary>
    /// The complete effective surface, pinned so issue #305's move off the persistence
    /// <c>OperatingExpenseCategory</c> cannot quietly add, drop or re-template a route. The two
    /// POSTs and the two PUTs are the JSON and multipart overloads of the same two operations;
    /// the API explorer lists both, as it always has.
    /// </summary>
    [Fact]
    public void Operating_expense_endpoints_keep_their_exact_routes_and_methods()
    {
        var routes = ApiContractTestHost.GetApiDescriptionsFor<OperatingExpensesController>()
            .Select(description => $"{description.HttpMethod?.ToUpperInvariant()} {description.RelativePath}")
            .OrderBy(route => route, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "DELETE api/operating-expenses/{id}",
                "GET api/operating-expenses",
                "GET api/operating-expenses/{id}",
                "GET api/operating-expenses/{id}/attachment",
                "POST api/operating-expenses",
                "POST api/operating-expenses",
                "PUT api/operating-expenses/{id}",
                "PUT api/operating-expenses/{id}",
            },
            routes);
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
