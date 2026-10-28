using Inventory.Application.PriceCatalogue;
using Inventory.Domain.PriceCatalogue;
using Inventory.Domain.Suppliers;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational tests for the collector work list (issue #924, parent #817): what one run is issued
/// with, what it stores, and what the response may and may not carry.
///
/// <para><strong>Two businesses throughout, and the denied scope a collector request carries.</strong>
/// The whole point of the parent issue is that five businesses' five copies of one product are one
/// search, so the fixture's two differently spelled colas run through every test here - and the use
/// case is composed over the real adapters on a context with no business at all, which is the scope
/// <c>GET api/price-collector/work</c> actually has. Relational SQLite over the real migration
/// history rather than InMemory, because the properties under test are the unique index on a run's
/// work, the foreign keys, the check constraints and one transaction.</para>
///
/// <para><strong>What is asserted about the response is mostly what is absent from it.</strong> No
/// business id, no business name, no business product id and no price - which is the issue's own
/// criterion, and the reason the work list is allowed to be read across businesses at all.</para>
/// </summary>
public class PriceCollectorWorkListTests
{
    private static readonly DateTime IssuedAt = new(2026, 10, 28, 19, 0, 0, DateTimeKind.Utc);

    #region One product, however many businesses track it

    /// <summary>
    /// The criterion: two businesses tracking the same product produce one matched product, with the
    /// listings the catalogue holds for it - and one work item per due supplier under it.
    /// </summary>
    [Fact]
    public async Task A_product_two_businesses_track_appears_once_with_its_known_listings()
    {
        await using var fixture = await PriceCatalogueFixture.CreateWithNoCollectableSuppliersAsync();
        await fixture.SetSupplierCollectionAsync(fixture.SupplierPresetId, SupplierPriceConnector.Kmart);

        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var firstListing = await fixture.SeedListingAsync(cola, "SKU-1", unitsPerPack: 30);
        var secondListing = await fixture.SeedListingAsync(cola, "SKU-2", unitsPerPack: 24);

        var mine = await fixture.TrackAsync(
            fixture.FirstBusinessId, fixture.FirstBusinessColaId, "Coca-Cola Classic", "375mL");
        var theirs = await fixture.TrackAsync(
            fixture.SecondBusinessId, fixture.SecondBusinessColaId, "coca-cola  classic", "375 ML");
        await fixture.MatchAsync(mine.Id, cola);
        await fixture.MatchAsync(theirs.Id, cola);

        var workList = await IssueAsync(fixture);

        var product = Assert.Single(workList.MatchedProducts);
        Assert.Equal(cola, product.MasterProductId);
        Assert.Equal("Coca-Cola", product.Brand);
        Assert.Equal("Classic", product.Name);
        Assert.Equal(
            [(firstListing, 30), (secondListing, 24)],
            product.KnownListings.Select(listing => (listing.ListingId, listing.UnitsPerPack)));
        Assert.Empty(workList.WaitingProducts);

        // One work item, because one supplier is due - and it names the only supplier there is.
        var workItem = Assert.Single(product.WorkItems);
        Assert.Equal(fixture.SupplierPresetId, workItem.SupplierPresetId);
    }

    /// <summary>
    /// And a product awaiting a match appears once as its folded search text, with its waiting key,
    /// the proposals a previous run already found under that key, and the master products a Super
    /// Admin refused for it.
    /// </summary>
    [Fact]
    public async Task A_waiting_product_appears_once_with_its_proposals_and_its_refusals()
    {
        await using var fixture = await PriceCatalogueFixture.CreateWithNoCollectableSuppliersAsync();
        await fixture.SetSupplierCollectionAsync(fixture.SupplierPresetId, SupplierPriceConnector.Kmart);

        var waitingKey = PriceCatalogueWaitingKey.For("Coca-Cola Classic", "375mL");
        await fixture.SeedWaitingListingAsync(waitingKey, "SKU-PROPOSED");

        var rejected = await fixture.SeedMasterProductAsync("Pepsi", "Max");
        var undone = await fixture.SeedMasterProductAsync("Solo", "Lemon");
        var accepted = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        await fixture.SeedMatchSuggestionAsync(waitingKey, rejected, ProductMatchSuggestionStatus.Rejected);
        await fixture.SeedMatchSuggestionAsync(waitingKey, undone, ProductMatchSuggestionStatus.Undone);
        await fixture.SeedMatchSuggestionAsync(waitingKey, accepted, ProductMatchSuggestionStatus.Tentative);

        await fixture.TrackAsync(
            fixture.FirstBusinessId, fixture.FirstBusinessColaId, "Coca-Cola Classic", "375mL");
        await fixture.TrackAsync(
            fixture.SecondBusinessId, fixture.SecondBusinessColaId, "coca-cola  classic", "375 ML");

        var workList = await IssueAsync(fixture);

        var product = Assert.Single(workList.WaitingProducts);
        Assert.Equal(waitingKey, product.WaitingKey);
        Assert.Equal("Coca-Cola Classic", product.SearchName);
        Assert.Equal("375mL", product.UnitLabel);
        Assert.Equal("SKU-PROPOSED", Assert.Single(product.ProposedListings).SupplierProductId);

        // The rejected and undone pairs, and only those: a tentative suggestion is the matcher's own
        // opinion rather than a person's refusal, and re-sending it as one would be a refusal nobody
        // made.
        Assert.Equal([rejected, undone], product.RefusedMasterProductIds.Order());
        Assert.Single(product.WorkItems);
    }

    #endregion

    #region The matching list

    /// <summary>
    /// The master products to match against are sent while something is waiting, and not otherwise:
    /// the only thing that list is for is matching a name nothing has matched yet.
    /// </summary>
    [Fact]
    public async Task The_master_product_list_is_sent_only_while_something_is_waiting()
    {
        await using var fixture = await PriceCatalogueFixture.CreateWithNoCollectableSuppliersAsync();
        await fixture.SetSupplierCollectionAsync(fixture.SupplierPresetId, SupplierPriceConnector.Kmart);

        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var matched = await fixture.TrackAsync(
            fixture.FirstBusinessId, fixture.FirstBusinessColaId, "Coca-Cola Classic", "375mL");
        await fixture.MatchAsync(matched.Id, cola);

        // Nothing is waiting: one matched product, and no vocabulary to match anything against.
        var withNothingWaiting = await IssueAsync(fixture);
        Assert.Empty(withNothingWaiting.WaitingProducts);
        Assert.Empty(withNothingWaiting.MasterProductsForMatching);

        // Now one product is waiting, and the master products arrive with it - description only.
        await fixture.TrackAsync(
            fixture.SecondBusinessId, fixture.SecondBusinessColaId, "coca-cola  classic", "375 ML");

        var withSomethingWaiting = await IssueAsync(fixture);

        Assert.Single(withSomethingWaiting.WaitingProducts);
        var master = Assert.Single(withSomethingWaiting.MasterProductsForMatching);
        Assert.Equal(cola, master.Id);
        Assert.Equal(("Coca-Cola", "Classic", 375m, MasterProductSizeUnit.Millilitre),
            (master.Brand, master.Name, master.SizeValue, master.SizeUnit));
    }

    #endregion

    #region Which suppliers are due

    /// <summary>
    /// A supplier nothing collects is not work, so a run with no collectable supplier issues no work
    /// at all - and publishes no product either, because every published product has to be answerable
    /// through a work item.
    /// </summary>
    [Fact]
    public async Task A_supplier_nothing_collects_is_not_due_and_no_work_is_issued()
    {
        await using var fixture = await PriceCatalogueFixture.CreateWithNoCollectableSuppliersAsync();

        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var matched = await fixture.TrackAsync(
            fixture.FirstBusinessId, fixture.FirstBusinessColaId, "Coca-Cola Classic", "375mL");
        await fixture.MatchAsync(matched.Id, cola);

        var workList = await IssueAsync(fixture);

        Assert.Empty(workList.Suppliers);
        Assert.Empty(workList.MatchedProducts);
        Assert.Empty(workList.WaitingProducts);
        Assert.Empty(await fixture.AllWorkItemsAsync());
        Assert.False(workList.WorkItemLimitReached);
    }

    /// <summary>
    /// Every due supplier's collection route travels with the run: the connector, its key where the
    /// route takes one, and the site URL the JB Metro route talks to. A product due at two suppliers
    /// is two work items, one per pair.
    /// </summary>
    [Fact]
    public async Task Each_due_supplier_publishes_its_route_and_each_pair_is_its_own_work_item()
    {
        await using var fixture = await PriceCatalogueFixture.CreateWithNoCollectableSuppliersAsync();
        await fixture.SetSupplierCollectionAsync(
            fixture.SupplierPresetId, SupplierPriceConnector.Varnmill, connectorKey: "coles");

        var jbMetro = await fixture.SeedSupplierPresetAsync("Beta Electronics");
        await fixture.SetSupplierCollectionAsync(
            jbMetro, SupplierPriceConnector.JbMetro, url: "https://beta.example.test/");

        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var matched = await fixture.TrackAsync(
            fixture.FirstBusinessId, fixture.FirstBusinessColaId, "Coca-Cola Classic", "375mL");
        await fixture.MatchAsync(matched.Id, cola);

        var workList = await IssueAsync(fixture);

        Assert.Equal(
            [
                (fixture.SupplierPresetId, SupplierPriceConnector.Varnmill, "coles", null),
                (jbMetro, SupplierPriceConnector.JbMetro, null, "https://beta.example.test/"),
            ],
            workList.Suppliers.Select(supplier =>
                (supplier.SupplierPresetId, supplier.PriceConnector, supplier.PriceConnectorKey, supplier.SiteUrl)));

        var product = Assert.Single(workList.MatchedProducts);
        Assert.Equal(
            [fixture.SupplierPresetId, jbMetro],
            product.WorkItems.Select(item => item.SupplierPresetId));
    }

    #endregion

    #region What is stored

    /// <summary>
    /// Every published work item is a stored row of this run, with an <strong>empty</strong> outcome
    /// and ids that are unique within it. The outcome is what #937 writes; issuing one only ever
    /// writes the absence.
    /// </summary>
    [Fact]
    public async Task Every_work_item_is_stored_with_no_outcome_and_an_id_unique_within_the_run()
    {
        await using var fixture = await PriceCatalogueFixture.CreateWithNoCollectableSuppliersAsync();
        await fixture.SetSupplierCollectionAsync(fixture.SupplierPresetId, SupplierPriceConnector.Kmart);
        var second = await fixture.SeedSupplierPresetAsync("Beta Grocer");
        await fixture.SetSupplierCollectionAsync(second, SupplierPriceConnector.BigW);

        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var matched = await fixture.TrackAsync(
            fixture.FirstBusinessId, fixture.FirstBusinessColaId, "Coca-Cola Classic", "375mL");
        await fixture.MatchAsync(matched.Id, cola);
        await fixture.TrackAsync(
            fixture.SecondBusinessId, fixture.SecondBusinessColaId, "Pepsi Max", "375mL");

        var workList = await IssueAsync(fixture);

        var published = workList.MatchedProducts
            .SelectMany(product => product.WorkItems)
            .Concat(workList.WaitingProducts.SelectMany(product => product.WorkItems))
            .ToList();

        // Two products and two due suppliers: four pairs, four ids, all distinct.
        Assert.Equal(4, published.Count);
        Assert.Equal(published.Count, published.Select(item => item.WorkItemId).Distinct().Count());

        var stored = await fixture.AllWorkItemsAsync();

        Assert.Equal(
            published.Select(item => item.WorkItemId).Order(),
            stored.Select(item => item.Id).Order());
        Assert.All(stored, item =>
        {
            Assert.Equal(workList.RunId, item.PriceCollectorRunId);
            Assert.Equal(IssuedAt, item.IssuedAtUtc);
            Assert.Null(item.Outcome);
            Assert.Null(item.OutcomeAtUtc);
            Assert.True(
                PriceCollectorWorkItemPolicy.HasExactlyOneProduct(item.MasterProductId, item.WaitingKey),
                "A stored work item names exactly one product.");
        });
    }

    /// <summary>
    /// Issuing a new run expires any run still open, and the run it issues carries the configured
    /// lifetime. An expired run keeps its own work items: expiring records that nobody reported, and
    /// removes nothing.
    /// </summary>
    [Fact]
    public async Task Issuing_a_run_expires_the_one_still_open_and_keeps_its_work()
    {
        await using var fixture = await PriceCatalogueFixture.CreateWithNoCollectableSuppliersAsync();
        await fixture.SetSupplierCollectionAsync(fixture.SupplierPresetId, SupplierPriceConnector.Kmart);

        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var matched = await fixture.TrackAsync(
            fixture.FirstBusinessId, fixture.FirstBusinessColaId, "Coca-Cola Classic", "375mL");
        await fixture.MatchAsync(matched.Id, cola);

        var clock = new FakeClock(IssuedAt);
        var first = await IssueAsync(fixture, clock);

        clock.UtcNow = IssuedAt.AddMinutes(30);
        var second = await IssueAsync(fixture, clock);

        Assert.NotEqual(first.RunId, second.RunId);

        var runs = await fixture.AllRunsAsync();

        var expired = Assert.Single(runs, run => run.Id == first.RunId);
        Assert.Equal(PriceCollectorRunStatus.Expired, expired.Status);
        Assert.Equal(clock.UtcNow, expired.CompletedAtUtc);

        var open = Assert.Single(runs, run => run.Id == second.RunId);
        Assert.Equal(PriceCollectorRunStatus.Running, open.Status);
        Assert.Null(open.CompletedAtUtc);
        Assert.Equal(clock.UtcNow.AddHours(PriceCollectorRunPolicy.DefaultExpiryHours), open.ExpiresAtUtc);
        Assert.Equal(open.ExpiresAtUtc, second.ExpiresAtUtc);

        // At most one open run, which is the invariant #937 reads, and the expired run's work is
        // still there to account for.
        Assert.Single(runs, run => run.Status == PriceCollectorRunStatus.Running);
        var stored = await fixture.AllWorkItemsAsync();
        Assert.Contains(stored, item => item.PriceCollectorRunId == first.RunId);
        Assert.Contains(stored, item => item.PriceCollectorRunId == second.RunId);
    }

    /// <summary>
    /// The run's lifetime is the configured one, which is what a deployment's own App Service setting
    /// decides - and it is stored on the run, so editing the setting cannot retroactively expire or
    /// revive work that was already issued.
    /// </summary>
    [Fact]
    public async Task The_runs_lifetime_is_the_configured_one()
    {
        await using var fixture = await PriceCatalogueFixture.CreateWithNoCollectableSuppliersAsync();
        await fixture.SetSupplierCollectionAsync(fixture.SupplierPresetId, SupplierPriceConnector.Kmart);

        var workList = await IssueAsync(fixture, new FakeClock(IssuedAt), runExpiryHours: 12);

        Assert.Equal(IssuedAt.AddHours(12), workList.ExpiresAtUtc);
        Assert.Equal(
            IssuedAt.AddHours(12),
            Assert.Single(await fixture.AllRunsAsync()).ExpiresAtUtc);
    }

    #endregion

    #region No business data leaves

    /// <summary>
    /// The issue's criterion, asserted over the whole serialized response rather than field by field:
    /// neither business's name, neither business's product id and no business id appears anywhere in
    /// the work list, even though both businesses' mappings produced it.
    /// </summary>
    [Fact]
    public async Task No_business_id_name_product_id_or_price_appears_in_the_work_list()
    {
        await using var fixture = await PriceCatalogueFixture.CreateWithNoCollectableSuppliersAsync();
        await fixture.SetSupplierCollectionAsync(fixture.SupplierPresetId, SupplierPriceConnector.Kmart);

        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        await fixture.SeedListingAsync(cola, "SKU-1", unitsPerPack: 30);
        var mine = await fixture.TrackAsync(
            fixture.FirstBusinessId, fixture.FirstBusinessColaId, "Coca-Cola Classic", "375mL");
        await fixture.MatchAsync(mine.Id, cola);
        await fixture.TrackAsync(
            fixture.SecondBusinessId, fixture.SecondBusinessColaId, "coca-cola  classic", "375 ML");

        var workList = await IssueAsync(fixture);
        var serialized = System.Text.Json.JsonSerializer.Serialize(
            InventoryApi.DTOs.PriceCollectorWorkResponse.From(workList));

        // No business of either business: not its name, not its key, and not its own product's key.
        // The product keys that *are* in there are the shared catalogue's - a master product id and a
        // supplier's own id for a listing - which are facts about products, and the type test below is
        // what states that there is nowhere for a business's own to go.
        foreach (var absent in new[]
        {
            "Coastal Vending",
            "Vending Co",
            "businessId",
            "businessName",
            "businessProductId",

            // And no price of any kind: an observed price is #925's, and the collector is the thing
            // that reports prices rather than the thing that is told them.
            "unitPrice",
            "packPrice",
            "observ",
        })
        {
            Assert.DoesNotContain(absent, serialized, StringComparison.OrdinalIgnoreCase);
        }

        // And it did read both businesses' mappings, so the absence above is not an empty answer.
        Assert.Single(workList.MatchedProducts);
        Assert.Single(workList.WaitingProducts);
    }

    /// <summary>
    /// The response types have nowhere to carry a business, which is the same claim made structurally:
    /// a business id can only travel where a type has room for it.
    /// </summary>
    [Fact]
    public void The_published_work_list_types_have_nowhere_to_carry_a_business()
    {
        Assert.Equal(
            [
                "ExpiresAtUtc",
                "IssuedAtUtc",
                "MasterProductsForMatching",
                "MatchedProducts",
                "RunId",
                "Suppliers",
                "WaitingProducts",
                "WorkItemLimitReached",
            ],
            typeof(PriceCollectorWorkList)
                .GetProperties()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));

        Assert.Equal(
            ["Brand", "KnownListings", "MasterProductId", "Name", "SizeUnit", "SizeValue", "WorkItems"],
            typeof(PriceCollectorMatchedProduct)
                .GetProperties()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));

        Assert.Equal(
            [
                "ProposedListings",
                "RefusedMasterProductIds",
                "SearchName",
                "UnitLabel",
                "WaitingKey",
                "WorkItems",
            ],
            typeof(PriceCollectorWaitingProduct)
                .GetProperties()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
    }

    #endregion

    #region The response is bounded

    /// <summary>
    /// A catalogue with more work than one run may be issued with yields a deterministic prefix and
    /// says so. Both halves matter: a silent prefix reads as "this is everything there is to search",
    /// and a product published without its work items would be a product no result could reference.
    /// </summary>
    [Fact]
    public async Task More_work_than_a_run_may_carry_is_a_flagged_deterministic_prefix()
    {
        await using var fixture = await PriceCatalogueFixture.CreateWithNoCollectableSuppliersAsync();
        await fixture.SetSupplierCollectionAsync(fixture.SupplierPresetId, SupplierPriceConnector.Kmart);

        // One product more than the budget allows, with one supplier due, so the budget is the
        // number of products this run may carry.
        const int Budget = 3;

        for (var index = 0; index < Budget + 1; index++)
        {
            var product = await fixture.SeedProductAsync(
                fixture.FirstBusinessId, $"Product {index}", "375mL");
            await fixture.TrackAsync(fixture.FirstBusinessId, product, $"Product {index}", "375mL");
        }

        var workList = await IssueAsync(fixture, budget: Budget);

        Assert.True(workList.WorkItemLimitReached);
        Assert.Equal(Budget, workList.WaitingProducts.Count);
        Assert.All(workList.WaitingProducts, product => Assert.Single(product.WorkItems));
        Assert.Equal(Budget, (await fixture.AllWorkItemsAsync()).Count);

        // And the prefix is the same prefix on a second read, because the order is the comparison
        // key's rather than the database's discretion.
        var again = await IssueAsync(fixture, budget: Budget);
        Assert.Equal(
            workList.WaitingProducts.Select(product => product.WaitingKey),
            again.WaitingProducts.Select(product => product.WaitingKey));
    }

    #endregion

    private static Task<PriceCollectorWorkList> IssueAsync(
        PriceCatalogueFixture fixture,
        FakeClock? clock = null,
        int runExpiryHours = PriceCollectorRunPolicy.DefaultExpiryHours,
        int? budget = null) =>
        new IssuePriceCollectorWorkList(
                fixture.DeniedWorkListStore(),
                fixture.DeniedRunStore(),
                new FixedRunSettings(runExpiryHours),
                clock ?? new FakeClock(IssuedAt),
                budget ?? PriceCollectorWorkListPolicy.MaxWorkItemsPerRun)
            .Handle(CancellationToken.None);

    private sealed class FixedRunSettings(int runExpiryHours) : IPriceCollectorRunSettings
    {
        public int RunExpiryHours { get; } = runExpiryHours;
    }
}
