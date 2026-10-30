using Inventory.Application.PriceCatalogue;
using Inventory.Domain.PriceCatalogue;
using Inventory.Domain.Subscriptions;
using Inventory.Domain.Suppliers;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Whose tracked products the shared collector is issued work for (issue #977, child of #965, after
/// #966 and #967): the collection half of that issue, over the two businesses and the denied scope
/// a collector request actually carries.
///
/// <para><strong>Relational, and for the same reason the rest of this folder is.</strong> What is
/// under test is which mappings a cross-business read reaches once a plan decides whether their
/// business is collected for - a question about a statement over the real schema, the real tenant
/// query filters and the real plan records, which an InMemory provider cannot answer.</para>
///
/// <para><strong>Two businesses throughout, on different plans.</strong> The criterion is that a
/// product tracked by both a Free and an entitled business is still collected once, which is a
/// statement about what two businesses' mappings do together and cannot be expressed with one.</para>
/// </summary>
public class PriceCollectorPlanEligibilityTests
{
    private static readonly DateTime IssuedAt = PriceCatalogueFixture.CreatedAt;

    #region Matched products

    /// <summary>
    /// The criterion: a matched product only a Free business tracks is not issued to the collector,
    /// and nothing about it is removed - the mapping, the master product and the listings are all
    /// exactly as they were, so the product is collected again the moment the plan includes it.
    /// </summary>
    [Fact]
    public async Task A_matched_product_only_a_free_business_tracks_is_not_issued()
    {
        await using var fixture = await PriceCatalogueFixture.CreateWithNoCollectableSuppliersAsync();
        await fixture.SetSupplierCollectionAsync(fixture.SupplierPresetId, SupplierPriceConnector.Kmart);
        await fixture.CompleteGrandfatheringAsync();

        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        await fixture.SeedListingAsync(cola, "SKU-1", unitsPerPack: 30);
        var mine = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.MatchAsync(mine.Id, cola);

        // No grant and no subscription at all, which is how a business really arrives on Free.
        var products = await EnforcingStore(fixture)
            .ListTrackedMasterProductsAsync(CancellationToken.None);

        Assert.Empty(products);

        // And nothing was deleted to arrive at that answer: the shared catalogue and the business's
        // own request are untouched (issue #977's fifth criterion).
        Assert.Single(await fixture.AllTrackingsAsync());
        Assert.Single(await fixture.AllMasterProductsAsync());
        Assert.Single(await fixture.AllListingsAsync());
    }

    /// <summary>
    /// The same product, once the business holds a Starter grant: issued, with its listings, exactly
    /// as it is today. The pair with the test above is what makes the first an exclusion rather than
    /// a broken read.
    /// </summary>
    [Theory]
    [InlineData(SubscriptionPlan.Starter)]
    [InlineData(SubscriptionPlan.Pro)]
    public async Task A_matched_product_an_entitled_business_tracks_is_issued(SubscriptionPlan plan)
    {
        await using var fixture = await PriceCatalogueFixture.CreateWithNoCollectableSuppliersAsync();
        await fixture.SetSupplierCollectionAsync(fixture.SupplierPresetId, SupplierPriceConnector.Kmart);
        await fixture.CompleteGrandfatheringAsync();
        await fixture.GrantPlanAsync(fixture.FirstBusinessId, plan);

        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var mine = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.MatchAsync(mine.Id, cola);

        var products = await EnforcingStore(fixture)
            .ListTrackedMasterProductsAsync(CancellationToken.None);

        Assert.Equal([cola], products.Select(product => product.Id));
    }

    /// <summary>
    /// The criterion's second half: a product tracked by both a Free and an entitled business is
    /// still collected, and still only once. One entitled business is what the collection is for;
    /// the Free business's own mapping neither adds a second search nor withholds the first.
    /// </summary>
    [Fact]
    public async Task A_product_tracked_by_a_free_and_an_entitled_business_is_still_collected_once()
    {
        await using var fixture = await PriceCatalogueFixture.CreateWithNoCollectableSuppliersAsync();
        await fixture.SetSupplierCollectionAsync(fixture.SupplierPresetId, SupplierPriceConnector.Kmart);
        await fixture.CompleteGrandfatheringAsync();
        await fixture.GrantPlanAsync(fixture.SecondBusinessId, SubscriptionPlan.Starter);

        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var mine = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        var theirs = await fixture.TrackAsync(fixture.SecondBusinessId, fixture.SecondBusinessColaId);
        await fixture.MatchAsync(mine.Id, cola);
        await fixture.MatchAsync(theirs.Id, cola);

        var workList = await IssueAsync(fixture);

        var product = Assert.Single(workList.MatchedProducts);
        Assert.Equal(cola, product.MasterProductId);
        Assert.Empty(workList.WaitingProducts);
    }

    /// <summary>
    /// An entitlement is the whole entitlement rule and not only the grants: a business entitled by
    /// an active paid subscription is collected for, and one whose subscription is cancelled is not.
    /// </summary>
    [Theory]
    [InlineData(BillingSubscriptionStatus.Active, true)]
    [InlineData(BillingSubscriptionStatus.Cancelled, false)]
    public async Task A_paid_subscription_decides_collection_the_same_way_a_grant_does(
        BillingSubscriptionStatus status,
        bool collected)
    {
        await using var fixture = await PriceCatalogueFixture.CreateWithNoCollectableSuppliersAsync();
        await fixture.SetSupplierCollectionAsync(fixture.SupplierPresetId, SupplierPriceConnector.Kmart);
        await fixture.CompleteGrandfatheringAsync();
        await fixture.SubscribeAsync(fixture.FirstBusinessId, SubscriptionPlan.Starter, status);

        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var mine = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.MatchAsync(mine.Id, cola);

        var products = await EnforcingStore(fixture)
            .ListTrackedMasterProductsAsync(CancellationToken.None);

        Assert.Equal(collected ? [cola] : [], products.Select(product => product.Id));
    }

    /// <summary>
    /// A grant that has ended entitles nothing, which is the half-open period rule the entitlement
    /// policy already owns: the business is on Free from the end instant, so its products stop being
    /// collected from the next run rather than from some separate expiry of their own.
    /// </summary>
    [Fact]
    public async Task A_grant_that_has_ended_stops_the_product_being_collected()
    {
        await using var fixture = await PriceCatalogueFixture.CreateWithNoCollectableSuppliersAsync();
        await fixture.SetSupplierCollectionAsync(fixture.SupplierPresetId, SupplierPriceConnector.Kmart);
        await fixture.CompleteGrandfatheringAsync();
        await fixture.GrantPlanAsync(
            fixture.FirstBusinessId,
            SubscriptionPlan.Starter,
            endsAtUtc: IssuedAt.AddDays(-1));

        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var mine = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.MatchAsync(mine.Id, cola);

        Assert.Empty(
            await EnforcingStore(fixture).ListTrackedMasterProductsAsync(CancellationToken.None));
    }

    #endregion

    #region Waiting keys

    /// <summary>
    /// The same rule for a product nothing has matched yet, which is decided for the folded waiting
    /// <em>key</em> rather than for a product: a key only a Free business's product folds to is not
    /// issued, and the key an entitled business's product folds to is.
    /// </summary>
    [Fact]
    public async Task A_waiting_key_is_issued_only_while_an_entitled_business_folds_to_it()
    {
        await using var fixture = await PriceCatalogueFixture.CreateWithNoCollectableSuppliersAsync();
        await fixture.SetSupplierCollectionAsync(fixture.SupplierPresetId, SupplierPriceConnector.Kmart);
        await fixture.CompleteGrandfatheringAsync();
        await fixture.GrantPlanAsync(fixture.SecondBusinessId, SubscriptionPlan.Starter);

        // The first business's Pepsi folds to a key nobody else holds; both businesses' colas fold
        // to one key, and only the second business is entitled.
        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessPepsiId);
        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.TrackAsync(fixture.SecondBusinessId, fixture.SecondBusinessColaId);

        var waiting = await EnforcingStore(fixture)
            .ListProductsAwaitingMatchAsync(CancellationToken.None);

        Assert.Equal(
            [PriceCatalogueWaitingKey.For("Coca-Cola Classic", "375mL")],
            waiting.Select(entry => entry.WaitingKey));

        // The entitled business's own spelling is what is sent, because its mapping is the only one
        // the read reaches at all - and the Free business's row is still there.
        Assert.Equal("coca-cola  classic", Assert.Single(waiting).SearchName);
        Assert.Equal(3, (await fixture.AllTrackingsAsync()).Count);
    }

    /// <summary>
    /// And the issued work list carries no business identity in the mixed-plan case either (issue
    /// #977's sixth criterion). The eligibility is decided from the businesses' plans and nothing
    /// about a business leaves the adapter: there is no field in any of these records to put one in.
    /// </summary>
    [Fact]
    public async Task A_mixed_plan_work_list_carries_no_business_identity()
    {
        await using var fixture = await PriceCatalogueFixture.CreateWithNoCollectableSuppliersAsync();
        await fixture.SetSupplierCollectionAsync(fixture.SupplierPresetId, SupplierPriceConnector.Kmart);
        await fixture.CompleteGrandfatheringAsync();
        await fixture.GrantPlanAsync(fixture.SecondBusinessId, SubscriptionPlan.Starter);

        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.TrackAsync(fixture.SecondBusinessId, fixture.SecondBusinessColaId);

        var workList = await IssueAsync(fixture);
        var serialized = System.Text.Json.JsonSerializer.Serialize(workList);

        Assert.DoesNotContain("businessId", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Coastal Vending", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Vending Co", serialized, StringComparison.Ordinal);
    }

    #endregion

    #region Until enforcement is switched on

    /// <summary>
    /// The issue's last criterion, which is also the shipped state: with the rollout setting off
    /// both businesses' products are collected, exactly as they are today, and the businesses hold
    /// no plan record at all.
    /// </summary>
    [Fact]
    public async Task With_enforcement_off_every_business_is_collected_for()
    {
        await using var fixture = await PriceCatalogueFixture.CreateWithNoCollectableSuppliersAsync();
        await fixture.SetSupplierCollectionAsync(fixture.SupplierPresetId, SupplierPriceConnector.Kmart);

        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var mine = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.MatchAsync(mine.Id, cola);
        await fixture.TrackAsync(fixture.SecondBusinessId, fixture.SecondBusinessColaId);

        var store = fixture.DeniedWorkListStore();

        Assert.Equal(
            [cola],
            (await store.ListTrackedMasterProductsAsync(CancellationToken.None))
                .Select(product => product.Id));
        Assert.Single(await store.ListProductsAwaitingMatchAsync(CancellationToken.None));
    }

    /// <summary>
    /// And with the setting on but the grandfathering step unfinished, nothing is withheld either:
    /// enforcement cannot start before the businesses that were already trading hold their grant,
    /// which would otherwise stop collecting for every one of them at once.
    /// </summary>
    [Fact]
    public async Task With_the_grandfathering_step_unfinished_every_business_is_collected_for()
    {
        await using var fixture = await PriceCatalogueFixture.CreateWithNoCollectableSuppliersAsync();
        await fixture.SetSupplierCollectionAsync(fixture.SupplierPresetId, SupplierPriceConnector.Kmart);
        await fixture.StartGrandfatheringAsync();

        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var mine = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.MatchAsync(mine.Id, cola);

        Assert.Single(
            await EnforcingStore(fixture).ListTrackedMasterProductsAsync(CancellationToken.None));
    }

    #endregion

    #region A run already issued

    /// <summary>
    /// The issue's third criterion: a run already issued completes unchanged even if a business's
    /// plan changes during it. The results path judges a submitted row against the work item the run
    /// issued and asks no plan question at all, so the price read for a product whose only entitled
    /// business downgraded mid-run is still accepted - and eligibility applies from the next run,
    /// which the exclusion above is.
    /// </summary>
    [Fact]
    public async Task A_run_already_issued_is_completed_after_the_plan_changes()
    {
        await using var fixture = await PriceCatalogueFixture.CreateWithNoCollectableSuppliersAsync();
        await fixture.SetSupplierCollectionAsync(fixture.SupplierPresetId, SupplierPriceConnector.Kmart);
        await fixture.CompleteGrandfatheringAsync();
        await fixture.GrantPlanAsync(
            fixture.FirstBusinessId,
            SubscriptionPlan.Starter,
            endsAtUtc: IssuedAt.AddHours(1));

        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var listing = await fixture.SeedIdentifiedListingAsync(
            cola,
            waitingKey: null,
            "SKU-1",
            SupplierListingMatchStatus.Confirmed);
        var mine = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.MatchAsync(mine.Id, cola);

        // The run is issued while the grant still covers the instant.
        var workList = await IssueAsync(fixture);
        var workItem = Assert.Single(Assert.Single(workList.MatchedProducts).WorkItems);

        // Two hours later the grant has ended, so the business is on Free - and the reading the
        // collector was already sent out for is still a price.
        var observedAt = IssuedAt.AddHours(2);
        var receipt = await new RecordPriceCollectorResults(
                fixture.DeniedResultStore(),
                new FakeClock(observedAt))
            .Handle(
                new PriceCollectorResultBatch(
                    workList.RunId,
                    [
                        new PriceCollectorResultRowSubmission(
                            workItem.WorkItemId,
                            fixture.SupplierPresetId,
                            "SKU-1",
                            "Alpha Cola 375 ml",
                            Variant: "Classic",
                            SizeValue: 375m,
                            SizeUnit: MasterProductSizeUnit.Millilitre,
                            Barcode: "0012345678905",
                            BarcodeScope: BarcodeScope.Unit,
                            PackQuantity: 30,
                            PackPrice: 30m,
                            RegularPackPrice: null,
                            Currency: SupplierPriceObservationPolicy.Currency,
                            PriceBasis: SupplierPriceBasis.ExclusiveOfGst,
                            SupplierGstRate: null,
                            IsPromotion: false,
                            PromotionEndsAtUtc: null,
                            StockStatus: SupplierStockStatus.InStock,
                            ObservedAtUtc: observedAt,
                            SourceReference: "result-1",
                            SourceUrl: null),
                    ]),
                CancellationToken.None);

        var accepted = Assert.Single(receipt!.Accepted);
        Assert.Equal(listing, accepted.ListingId);
        Assert.Single(await fixture.AllObservationsAsync());

        // The next run is where eligibility applies: the business is on Free by now, so the product
        // is not issued again.
        Assert.Empty(
            await EnforcingStore(fixture, observedAt)
                .ListTrackedMasterProductsAsync(CancellationToken.None));
    }

    #endregion

    private static IPriceCatalogueWorkListStore EnforcingStore(
        PriceCatalogueFixture fixture,
        DateTime? nowUtc = null) =>
        fixture.DeniedWorkListStore(enforcePlans: true, nowUtc ?? IssuedAt);

    /// <summary>
    /// One run issued by the real use case over the enforcing store, which is how the eligibility
    /// reaches the published work list.
    /// </summary>
    private static Task<PriceCollectorWorkList> IssueAsync(PriceCatalogueFixture fixture) =>
        new IssuePriceCollectorWorkList(
                EnforcingStore(fixture),
                fixture.DeniedRunStore(),
                new FixedRunSettings(PriceCollectorRunPolicy.DefaultExpiryHours),
                new FakeClock(IssuedAt))
            .Handle(CancellationToken.None);

    private sealed class FixedRunSettings(int runExpiryHours) : IPriceCollectorRunSettings
    {
        public int RunExpiryHours { get; } = runExpiryHours;
    }
}
