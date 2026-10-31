using System.Net;
using System.Text.Json;
using Inventory.Domain.Subscriptions;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The Free/Starter/Pro catalogue <c>GET /api/me/plan-catalogue</c> reports (issue #970), over real
/// HTTP: every plan's price, storage allowance and connected-machine terms, read from the one
/// definition in <c>Inventory.Domain.Subscriptions.PlanCatalogue</c> rather than a second copy.
///
/// <c>CapabilityAuthorizationApiTests</c> already covers the access decision - every role holds
/// <c>Dashboard.View</c> - so this file is about the one thing that test does not check: the body
/// itself, and that it names no business.
/// </summary>
public sealed class MePlanCatalogueApiTests : IClassFixture<SuperAdminBusinessesApiFactory>, IAsyncLifetime
{
    private const string Uri = "/api/me/plan-catalogue";

    private readonly SuperAdminBusinessesApiFactory _factory;

    public MePlanCatalogueApiTests(SuperAdminBusinessesApiFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Reports_every_catalogue_plan_from_the_one_definition()
    {
        var response = await _factory.Authenticated(SuperAdminBusinessesApiFactory.TradingOwnerObjectId).GetAsync(Uri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var plans = json.GetProperty("plans").EnumerateArray().ToArray();

        Assert.Equal(PlanCatalogue.All.Count, plans.Length);
        for (var i = 0; i < PlanCatalogue.All.Count; i++)
        {
            var expected = PlanCatalogue.All[i];
            var actual = plans[i];

            Assert.Equal(
                [
                    "additionalConnectedMachinePriceMinorUnits",
                    "attachmentStorageLimitBytes",
                    "currencyCode",
                    "includedConnectedMachines",
                    "monthlyPriceMinorUnits",
                    "plan",
                ],
                actual.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
            Assert.Equal(expected.Plan.ToString(), actual.GetProperty("plan").GetString());
            Assert.Equal(expected.MonthlyPriceMinorUnits, actual.GetProperty("monthlyPriceMinorUnits").GetInt32());
            Assert.Equal(expected.CurrencyCode, actual.GetProperty("currencyCode").GetString());
            Assert.Equal(expected.AttachmentStorageLimitBytes, actual.GetProperty("attachmentStorageLimitBytes").GetInt64());
            Assert.Equal(expected.IncludedConnectedMachines, actual.GetProperty("includedConnectedMachines").GetInt32());
            if (expected.AdditionalConnectedMachinePriceMinorUnits is { } additional)
            {
                Assert.Equal(additional, actual.GetProperty("additionalConnectedMachinePriceMinorUnits").GetInt32());
            }
            else
            {
                Assert.Equal(JsonValueKind.Null, actual.GetProperty("additionalConnectedMachinePriceMinorUnits").ValueKind);
            }
        }
    }

    /// <summary>
    /// The catalogue is the same for every caller: it names no business, whatever the caller's own
    /// business is called.
    /// </summary>
    [Fact]
    public async Task Names_no_business()
    {
        var body = await (await _factory.Authenticated(SuperAdminBusinessesApiFactory.TradingOwnerObjectId).GetAsync(Uri))
            .Content.ReadAsStringAsync();

        Assert.DoesNotContain(SuperAdminBusinessesApiFactory.TradingBusinessName, body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every role sees the identical catalogue: it is not a per-business or per-role answer.
    /// </summary>
    [Fact]
    public async Task Is_identical_for_every_role()
    {
        var ownerBody = await (await _factory.Authenticated(SuperAdminBusinessesApiFactory.TradingOwnerObjectId).GetAsync(Uri))
            .Content.ReadAsStringAsync();
        var managerBody = await (await _factory.Authenticated(SuperAdminBusinessesApiFactory.TradingManagerObjectId).GetAsync(Uri))
            .Content.ReadAsStringAsync();

        Assert.Equal(ownerBody, managerBody);
    }
}
