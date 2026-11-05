using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Inventory.Domain.PriceCatalogue;
using Inventory.Domain.Suppliers;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;
using ApiFactory = InventoryApi.Tests.Auth.PriceCollectorTokenValidationTests.ApiFactory;
using CollectorTokens = InventoryApi.Tests.Auth.PriceCollectorTokenValidationTests.CollectorTokens;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The <c>schedule</c> query value of <c>GET /api/price-collector/work</c> (issue #1108), over the
/// real endpoint: <c>all</c> or nothing at all is today's work list, <c>due</c> is only the pairs
/// owed a search, and anything else is a <c>400</c> that issues nothing.
///
/// <para><strong>The contract is what is under test here, not the rule.</strong> Which pairs are due
/// is decided by <c>SupplierSearchDuePolicy</c> and asserted against the real tables in
/// <c>PriceCollectorDueWorkListTests</c>; what this suite adds is the part only the HTTP boundary
/// has: the vocabulary, the refusal, the default, and the shape of the answer when nothing is due -
/// a success with an empty list and <strong>no run id</strong>, which #1110 relies on.</para>
///
/// <para>The production pipeline, as <c>PriceCollectorMatchApiTests</c> uses it: the real collector
/// bearer scheme and policy, the real business-scope middleware (which leaves a collector request
/// denied), the real query filters, the real controller and one relational SQLite database.</para>
/// </summary>
public sealed class PriceCollectorWorkScheduleApiTests : IClassFixture<ApiFactory>
{
    private const string WorkEndpoint = "/api/price-collector/work";

    private static readonly DateTime SeededAt = new(2026, 11, 4, 4, 0, 0, DateTimeKind.Utc);

    private readonly ApiFactory _factory;

    public PriceCollectorWorkScheduleApiTests(ApiFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// An unrecognised value is refused with <c>400</c> and the Domain's own sentence, and
    /// <strong>nothing is issued</strong>: a typo in a scheduled run must not re-search the whole
    /// catalogue, and a typo in a manual one must not look like a run with nothing to do.
    /// </summary>
    [Theory]
    [InlineData("overdue")]
    [InlineData("none")]
    [InlineData("1")]
    [InlineData("al")]
    public async Task An_unknown_schedule_is_refused_and_issues_nothing(string schedule)
    {
        await SeedAsync();

        var before = await RunCountAsync();

        var response = await GetAsync($"{WorkEndpoint}?schedule={Uri.EscapeDataString(schedule)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            PriceCollectorWorkListPolicy.UnknownScheduleMessage,
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        Assert.Equal(before, await RunCountAsync());
    }

    /// <summary>
    /// The whole contract in one run, because the states follow one another: <c>all</c> with no value
    /// at all issues a run as it always did; <c>due</c> then has nothing to issue, because that run's
    /// work is in flight, and answers an empty list with no run id and creates no run; and <c>all</c>
    /// issues the same pair again anyway, which is the documented bypass.
    /// </summary>
    [Fact]
    public async Task The_schedule_values_select_the_work_and_the_empty_answer_carries_no_run()
    {
        await SeedAsync();

        // No value at all: the default, and exactly what this route answered before issue #1108.
        var byDefault = await ReadAsync(await GetAsync(WorkEndpoint));
        var defaultRunId = byDefault.GetProperty("runId").GetInt64();
        Assert.Equal(JsonValueKind.Number, byDefault.GetProperty("runId").ValueKind);
        Assert.Single(byDefault.GetProperty("waitingProducts").EnumerateArray());

        var runsAfterDefault = await RunCountAsync();

        // `due` now has nothing to do: that run is still open, so its pair is in flight.
        var nothingDue = await ReadAsync(await GetAsync($"{WorkEndpoint}?schedule=due"));

        Assert.Equal(JsonValueKind.Null, nothingDue.GetProperty("runId").ValueKind);
        Assert.Equal(JsonValueKind.Null, nothingDue.GetProperty("issuedAtUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, nothingDue.GetProperty("expiresAtUtc").ValueKind);
        Assert.Empty(nothingDue.GetProperty("suppliers").EnumerateArray());
        Assert.Empty(nothingDue.GetProperty("matchedProducts").EnumerateArray());
        Assert.Empty(nothingDue.GetProperty("waitingProducts").EnumerateArray());
        Assert.Empty(nothingDue.GetProperty("masterProductsForMatching").EnumerateArray());
        Assert.False(nothingDue.GetProperty("workItemLimitReached").GetBoolean());

        // And it created no run, so there is nothing for the workflow of #1110 to finalise and
        // nothing to expire into a page of "not searched" rows. The run the default call issued is
        // also still open: nothing was expired either.
        Assert.Equal(runsAfterDefault, await RunCountAsync());
        Assert.Equal(PriceCollectorRunStatus.Running, await RunStatusAsync(defaultRunId));

        // `all` issues the already in-flight pair again, on purpose: a manual full run bypasses the
        // due rule and the in-flight protection alike.
        var again = await ReadAsync(await GetAsync($"{WorkEndpoint}?schedule=all"));

        Assert.NotEqual(defaultRunId, again.GetProperty("runId").GetInt64());
        Assert.Single(again.GetProperty("waitingProducts").EnumerateArray());

        // Nothing a business owns is anywhere in either answer.
        foreach (var answer in new[] { byDefault, nothingDue, again })
        {
            Assert.DoesNotContain(
                ApiFactory.BusinessName,
                JsonSerializer.Serialize(answer),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The two accepted values are matched without regard to case or padding, because a query value
    /// is typed by hand and by a workflow file alike.
    /// </summary>
    [Theory]
    [InlineData("ALL")]
    [InlineData("All")]
    [InlineData("DUE")]
    [InlineData("Due")]
    [InlineData(" due ")]
    // A blank value is no value: the parameter was sent and says nothing, which is `all` for the
    // reason omitting it is - a manual run that states nothing gets today's behaviour.
    [InlineData("")]
    [InlineData(" ")]
    public async Task The_accepted_values_are_matched_without_regard_to_case(string schedule)
    {
        await SeedAsync();

        var response = await GetAsync($"{WorkEndpoint}?schedule={schedule}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<int> RunCountAsync()
    {
        await using var db = TestAppDbContext.Unrestricted(_factory.DatabaseOptions);

        return await db.PriceCollectorRuns.AsNoTracking().CountAsync();
    }

    private async Task<PriceCollectorRunStatus> RunStatusAsync(long runId)
    {
        await using var db = TestAppDbContext.Unrestricted(_factory.DatabaseOptions);

        return await db.PriceCollectorRuns
            .AsNoTracking()
            .Where(run => run.Id == runId)
            .Select(run => run.Status)
            .SingleAsync();
    }

    /// <summary>
    /// One business tracking one product, and one supplier the collector may search on a daily
    /// schedule whose effective-from instant is in the past - so the coverage boundary is the
    /// occurrence's and a pair that has never been answered is due.
    ///
    /// <para>The mapping and the preset are written directly, exactly as
    /// <c>PriceCollectorMatchApiTests</c> writes them: what is under test is the endpoint's contract,
    /// and arranging the state through the switch of #938 would need a member's token and prove
    /// nothing extra. Every shipped supplier master record has its collection route cleared, so the
    /// only due supplier is this test's own.</para>
    /// </summary>
    private async Task SeedAsync()
    {
        await _factory.SeedAsync();

        await using var db = TestAppDbContext.Unrestricted(_factory.DatabaseOptions);

        if (await db.ProductPriceTrackings.AnyAsync())
        {
            return;
        }

        foreach (var shipped in await db.SupplierPresets.ToListAsync())
        {
            shipped.PriceConnector = null;
            shipped.PriceConnectorKey = null;
        }

        var supplier = new SupplierPreset
        {
            Name = "Schedule Grocer",
            NormalisedName = SupplierPresetPolicy.NormalisedName("Schedule Grocer"),
            IsEnabled = true,
            SearchPrices = true,
            ServesNationwide = true,
            PriceConnector = SupplierPriceConnector.BigW,
            PricesIncludeGst = true,
            SearchFrequency = SupplierSearchFrequency.Daily,
            SearchDaysOfWeek = string.Empty,
            // Long before any run this suite makes, so an occurrence rather than a schedule change is
            // what the coverage boundary comes from.
            ScheduleEffectiveFromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            PriceCheckFreshnessHours = SupplierSearchSchedulePolicy.FreshnessHoursFor(
                SupplierSearchSchedulePolicy.Daily),
            CreatedAtUtc = SeededAt,
            UpdatedAtUtc = SeededAt,
        };

        db.SupplierPresets.Add(supplier);

        var product = new Product
        {
            BusinessId = 1,
            Name = "Coca-Cola Classic",
            Unit = "375mL",
            CreatedAt = SeededAt,
            UpdatedAt = SeededAt,
        };

        db.Products.Add(product);
        await db.SaveChangesAsync();

        db.ProductPriceTrackings.Add(new ProductPriceTracking
        {
            BusinessId = 1,
            ProductId = product.Id,
            Status = ProductPriceTrackingStatus.Requested,
            WaitingKey = PriceCatalogueWaitingKey.For("Coca-Cola Classic", "375mL"),
            SearchName = "Coca-Cola Classic",
            UnitLabel = "375mL",
            CreatedAtUtc = SeededAt,
            UpdatedAtUtc = SeededAt,
        });

        await db.SaveChangesAsync();
    }

    private async Task<HttpResponseMessage> GetAsync(string path)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CollectorTokens.AppOnly());

        return await client.GetAsync(path);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(body);

        return document.RootElement.Clone();
    }
}
