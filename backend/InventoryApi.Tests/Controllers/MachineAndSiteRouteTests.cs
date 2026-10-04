#nullable enable
using InventoryApi.Controllers;
using InventoryApi.Tests.Swagger;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// Locks the HTTP surface of the machine and site endpoints across issue #302's rewiring, which
/// replaced the <c>MachineService</c>/<c>SiteService</c> delegators with the Application use cases
/// and the EF entity responses with API-owned DTOs. Routes are an acceptance criterion of that
/// issue, and a controller whose constructor, return types and response mapping all change at once
/// is exactly where a route template or an HTTP method could move unnoticed. These tests assert the
/// effective routes the MVC API explorer resolves, not the presence of an attribute.
/// </summary>
public class MachineAndSiteRouteTests
{
    [Theory]
    [InlineData("GET", "api/Machines")]
    [InlineData("GET", "api/Machines/{id}")]
    [InlineData("GET", "api/Machines/{id}/products")]
    [InlineData("POST", "api/Machines/{id}/sync-restock")]
    [InlineData("POST", "api/Machines/{id}/sync-restock/apply")]
    [InlineData("POST", "api/Machines/{id}/sync-restock/resolve-duplicate")]
    [InlineData("POST", "api/Machines/{id}/sync-restock/resolve-manual")]
    public void Machine_endpoint_is_available(string httpMethod, string relativePath) =>
        AssertEndpoint<MachinesController>(httpMethod, relativePath);

    [Theory]
    [InlineData("GET", "api/Sites")]
    [InlineData("GET", "api/Sites/{id}/products")]
    public void Site_endpoint_is_available(string httpMethod, string relativePath) =>
        AssertEndpoint<SitesController>(httpMethod, relativePath);

    /// <summary>
    /// The complete surface, so an action cannot be added or dropped silently either: adding or
    /// removing an endpoint is an API contract change issue #302 excludes.
    /// </summary>
    [Fact]
    public void Machine_and_site_controllers_expose_no_other_endpoint()
    {
        Assert.Equal(7, ApiContractTestHost.GetApiDescriptionsFor<MachinesController>().Count);
        Assert.Equal(2, ApiContractTestHost.GetApiDescriptionsFor<SitesController>().Count);
    }

    private static void AssertEndpoint<TController>(string httpMethod, string relativePath)
        where TController : class
    {
        var descriptions = ApiContractTestHost.GetApiDescriptionsFor<TController>();

        Assert.Contains(descriptions, description =>
            string.Equals(description.HttpMethod, httpMethod, StringComparison.OrdinalIgnoreCase) &&
            description.RelativePath == relativePath);
    }
}
