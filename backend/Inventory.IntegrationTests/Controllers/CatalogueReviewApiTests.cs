using System.Reflection;
using Inventory.Application.Access;
using InventoryApi.Auth;
using InventoryApi.Controllers;
using InventoryApi.Tests.Swagger;
using Microsoft.AspNetCore.Mvc.Controllers;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The published HTTP surface of the provider catalogue review (issue #738): the three routes, their
/// methods, and the capability each one declares.
///
/// <para>The capabilities are the part worth pinning, because they are a security decision rather
/// than a shape. Starting and applying a review require <c>Imports.Run</c> - a review <em>is</em> the
/// catalogue import, decided row by row - while attributing historical sales to the products a review
/// linked requires <c>Data.Repair</c>, an Owner alone, because it consumes costed inventory, moves
/// product-level revenue and replays cost history. A later change that gave the sale resolution the
/// import capability would widen who may recost history, which no diff should be able to do
/// quietly.</para>
///
/// <para>The behaviour behind these routes is asserted over a relational database in
/// <c>InventoryApi.Tests.Application.CatalogueReview</c>; this test is about the contract.</para>
/// </summary>
public class CatalogueReviewApiTests
{
    [Theory]
    [InlineData("POST", "api/catalogue-review", nameof(Capability.ImportsRun))]
    [InlineData("POST", "api/catalogue-review/apply", nameof(Capability.ImportsRun))]
    [InlineData("POST", "api/catalogue-review/unresolved-sales/apply", nameof(Capability.DataRepair))]
    public void Each_route_is_published_with_the_capability_it_requires(
        string method, string route, string capability)
    {
        var description = Assert.Single(
            ApiContractTestHost.GetApiDescriptionsFor<CatalogueReviewController>()
                .Where(candidate =>
                    string.Equals(candidate.HttpMethod, method, StringComparison.OrdinalIgnoreCase)
                    && candidate.RelativePath == route));

        var action = Assert.IsType<ControllerActionDescriptor>(description.ActionDescriptor);
        var declared = action.MethodInfo
            .GetCustomAttributes<RequireCapabilityAttribute>()
            .Select(attribute => attribute.Capability.ToString())
            .ToList();

        Assert.Equal([capability], declared);
    }

    /// <summary>
    /// Three routes and no more. A fourth action on this controller would be a new write surface over
    /// the catalogue or over historical sale attribution, which is a reviewed decision rather than an
    /// implementation detail.
    /// </summary>
    [Fact]
    public void The_controller_publishes_exactly_those_three_routes() =>
        Assert.Equal(
            [
                ("POST", "api/catalogue-review"),
                ("POST", "api/catalogue-review/apply"),
                ("POST", "api/catalogue-review/unresolved-sales/apply"),
            ],
            ApiContractTestHost.GetApiDescriptionsFor<CatalogueReviewController>()
                .Select(description => (
                    description.HttpMethod?.ToUpperInvariant() ?? string.Empty,
                    description.RelativePath ?? string.Empty))
                .OrderBy(route => route.Item2, StringComparer.Ordinal)
                .ToList());

    /// <summary>
    /// Every action declares a capability on itself, because this controller deliberately declares
    /// none at the class level: two of its endpoints are ordinary import work and one is a data
    /// repair, and a controller-level capability would make the stricter one look like the looser one.
    /// </summary>
    [Fact]
    public void No_capability_is_declared_at_the_controller_level() =>
        Assert.Empty(typeof(CatalogueReviewController).GetCustomAttributes<RequireCapabilityAttribute>());
}
