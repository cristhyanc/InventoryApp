using Inventory.Application.PriceCatalogue;
using Inventory.Application.Products;
using Inventory.Domain.PriceCatalogue;
using Inventory.Domain.Subscriptions;
using Inventory.Domain.Suppliers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Subscriptions;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// A migrated SQLite database with <strong>two</strong> businesses, their own products, and the
/// shared catalogue's platform rows, shared by the relational tests of issue #923's two adapters.
///
/// <para>Two businesses throughout, as AGENTS.md § Tenant ownership requires of any change to
/// ownership or enforcement: everything these tests are about is what a read or a write can reach
/// across a boundary, and a one-business fixture cannot express that. Relational SQLite over the real
/// migration history rather than InMemory, because the properties under test are the central query
/// filters, the ownership stamp, the unique indexes, the foreign keys and the check constraints -
/// none of which an InMemory provider has.</para>
///
/// <para>The two businesses' products are deliberately spelled differently - <c>"Coca-Cola
/// Classic"</c> and <c>"coca-cola  classic"</c> - because that is the case the whole deduplication
/// exists for: five businesses' five catalogues legitimately spell one product five ways, and the
/// collector must not search five times.</para>
/// </summary>
internal sealed class PriceCatalogueFixture : IAsyncDisposable
{
    public static readonly DateTime CreatedAt = new(2026, 10, 27, 5, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The instant the plan records this fixture writes apply from, before everything else here, so
    /// a grant covers <see cref="CreatedAt"/> - the instant the work-list stores are built with
    /// unless a test chooses another one (issue #977).
    /// </summary>
    public static readonly DateTime GrantedFrom = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    /// <summary>
    /// The contexts handed to an adapter under test, kept so they are disposed with the fixture
    /// rather than left to the connection's own lifetime.
    /// </summary>
    private readonly List<AppDbContext> _contexts = [];

    private PriceCatalogueFixture(SqliteConnection connection, DbContextOptions<AppDbContext> options)
    {
        _connection = connection;
        _options = options;
    }

    /// <summary>The first business, whose mappings most of these tests are about.</summary>
    public int FirstBusinessId { get; private set; }

    /// <summary>The second business, which must see none of the first's.</summary>
    public int SecondBusinessId { get; private set; }

    /// <summary>The first business's "Coca-Cola Classic" / "375mL".</summary>
    public long FirstBusinessColaId { get; private set; }

    /// <summary>The first business's "Pepsi Max" / "375mL".</summary>
    public long FirstBusinessPepsiId { get; private set; }

    /// <summary>
    /// The second business's own cola, spelled differently on purpose: <c>"coca-cola  classic"</c> /
    /// <c>"375 ML"</c> folds to the same waiting key as the first business's.
    /// </summary>
    public long SecondBusinessColaId { get; private set; }

    /// <summary>A supplier master record of this fixture's own, so no shipped preset is relied on.</summary>
    public int SupplierPresetId { get; private set; }

    public static async Task<PriceCatalogueFixture> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var fixture = new PriceCatalogueFixture(
            connection,
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);

        await using (var setup = TestAppDbContext.Unrestricted(fixture._options))
        {
            await setup.Database.MigrateAsync();
        }

        fixture.FirstBusinessId = await fixture.SeedBusinessAsync("Coastal Vending");
        fixture.SecondBusinessId = await fixture.SeedBusinessAsync("Vending Co");

        fixture.FirstBusinessColaId = await fixture.SeedProductAsync(
            fixture.FirstBusinessId,
            "Coca-Cola Classic",
            "375mL");
        fixture.FirstBusinessPepsiId = await fixture.SeedProductAsync(
            fixture.FirstBusinessId,
            "Pepsi Max",
            "375mL");
        fixture.SecondBusinessColaId = await fixture.SeedProductAsync(
            fixture.SecondBusinessId,
            "coca-cola  classic",
            "375 ML");

        // A name none of the nine shipped presets holds: those are inserted by their own migrations
        // and NormalisedName is unique.
        fixture.SupplierPresetId = await fixture.SeedSupplierPresetAsync("Alpha Grocer");

        return fixture;
    }

    /// <summary>
    /// The mapping store as a request would reach it: the real adapter on a context scoped to one
    /// business, which is the only way the stamp and the filters are under test at all.
    /// </summary>
    public IProductPriceTrackingStore TrackingStoreFor(int businessId) =>
        new EfProductPriceTrackingStore(Track(TestAppDbContext.For(_options, businessId)));

    /// <summary>The mapping store on a context whose caller has no usable membership.</summary>
    public IProductPriceTrackingStore DeniedTrackingStore() =>
        new EfProductPriceTrackingStore(Track(TestAppDbContext.Denied(_options)));

    /// <summary>The work-list store on a context scoped to one business.</summary>
    public IPriceCatalogueWorkListStore WorkListStoreFor(int businessId) =>
        WorkListStore(TestAppDbContext.For(_options, businessId), enforcePlans: false, nowUtc: null);

    /// <summary>
    /// The work-list store as a <strong>collector request</strong> reaches it: the real adapter on a
    /// context whose caller has no business at all.
    ///
    /// <para>That is request-shaped, and it is the shape that matters since issue #924. A collector
    /// request carries a denied <c>BusinessScope</c>, so the central filter matches none of the
    /// mappings - which is exactly why the two mapping reads make that issue's reviewed
    /// <c>IgnoreQueryFilters</c> statement over the mappings' business-free text columns. The
    /// deduplication <em>across</em> businesses, which is what issue #923's third and seventh criteria
    /// are about, is therefore asserted here over the scope the one endpoint that reaches this adapter
    /// actually has.</para>
    ///
    /// <para>Plan enforcement is <strong>off</strong> unless a test asks for it, which is the
    /// shipped state and issue #977's last criterion: until a deployment switches the rollout
    /// setting on, the work list is exactly what it was. <paramref name="enforcePlans"/> is that
    /// deployment setting and <paramref name="nowUtc"/> the instant the business's plan records are
    /// judged at.</para>
    /// </summary>
    public IPriceCatalogueWorkListStore DeniedWorkListStore(
        bool enforcePlans = false,
        DateTime? nowUtc = null) =>
        WorkListStore(TestAppDbContext.Denied(_options), enforcePlans, nowUtc);

    /// <summary>
    /// The work-list store over a context that sees every business's rows without any filter at all.
    /// Setup-shaped, kept so a test can state that the reviewed read answers the same thing a
    /// completely unfiltered one does - the read is bounded by the collector endpoint's
    /// authorization, not by the context it is given.
    /// </summary>
    public IPriceCatalogueWorkListStore UnscopedWorkListStore() =>
        WorkListStore(TestAppDbContext.Unrestricted(_options), enforcePlans: false, nowUtc: null);

    /// <summary>
    /// The work-list store as the composition root builds it: the real adapter, the deployment's
    /// rollout setting, the real grandfathering record on the same context, and the clock
    /// (issue #977). Every business is collected for while that setting is off.
    /// </summary>
    private IPriceCatalogueWorkListStore WorkListStore(
        AppDbContext context,
        bool enforcePlans,
        DateTime? nowUtc)
    {
        var db = Track(context);

        return new EfPriceCatalogueWorkListStore(
            db,
            enforcePlans
                ? FakeSubscriptionEnforcementFlag.Enabled
                : FakeSubscriptionEnforcementFlag.Disabled,
            new EfSubscriptionGrandfatheringStore(db),
            new FakeClock(nowUtc ?? CreatedAt));
    }

    /// <summary>The collector run store as a collector request reaches it: a denied scope.</summary>
    public IPriceCollectorRunStore DeniedRunStore() =>
        new EfPriceCollectorRunStore(Track(TestAppDbContext.Denied(_options)));

    /// <summary>
    /// The collector results store as a collector request reaches it (issue #937): the real adapter
    /// on a context whose caller has no business at all, which is the scope
    /// <c>POST api/price-collector/results</c> actually has.
    ///
    /// <para>A new context each time, deliberately: every call the collector makes is its own
    /// request, and a test that reused one change tracker would be asserting against entities the
    /// previous call left behind rather than against what was stored.</para>
    /// </summary>
    public IPriceCollectorResultStore DeniedResultStore() =>
        new EfPriceCollectorResultStore(Track(TestAppDbContext.Denied(_options)));

    /// <summary>
    /// The collector match store as a collector request reaches it (issue #941): the real adapter on
    /// a context whose caller has no business at all, which is the scope
    /// <c>POST api/price-collector/matches</c> actually has - and the whole reason that adapter needs
    /// the reviewed read and the narrow matching write it has.
    ///
    /// <para>A new context each time, for the reason the results store gets one: every call the
    /// collector makes is its own request.</para>
    /// </summary>
    public IPriceCollectorMatchStore DeniedMatchStore() =>
        new EfPriceCollectorMatchStore(Track(TestAppDbContext.Denied(_options)));

    /// <summary>
    /// The Super Admin review store as a platform-admin request reaches it (issue #928): the real
    /// adapter on a context whose caller has no business at all, which is the scope every
    /// <c>api/super-admin/price-catalogue-review</c> request has - and the whole reason that adapter
    /// needs the reviewed cross-business read and the narrow review write it has.
    ///
    /// <para>A new context each time, for the reason the collector stores get one: every call a
    /// reviewer makes is its own request, and a reused change tracker would assert against entities
    /// the previous call left behind rather than against what was stored.</para>
    /// </summary>
    public IPriceCatalogueReviewStore DeniedReviewStore() =>
        new EfPriceCatalogueReviewStore(Track(TestAppDbContext.Denied(_options)));

    /// <summary>
    /// The same store on a context scoped to <em>one business</em>, for the test that says a lease
    /// cannot be taken there: a review decision is a platform decision, and a request that already
    /// names a business must never write another business's mapping through it.
    /// </summary>
    public IPriceCatalogueReviewStore ReviewStoreFor(int businessId) =>
        new EfPriceCatalogueReviewStore(Track(TestAppDbContext.For(_options, businessId)));

    /// <summary>
    /// The read-only master-product catalogue store as a platform-admin request reaches it (issue
    /// #1023): the real adapter on a context whose caller has no business at all, which is the scope
    /// every <c>api/super-admin/master-products</c> request has. The write half of the same surface is
    /// <see cref="DeniedMasterProductWriteStore"/> (issue #1024).
    ///
    /// <para>That is the shape that matters here too. The adapter reads four platform tables and no
    /// tenant-owned one, so unlike the review store beside it, it needs no filter-ignoring read and no
    /// write lease - and a denied scope is what proves it: a mapping, a product and a business are
    /// invisible to it, and its answers still carry everything the catalogue page needs.</para>
    /// </summary>
    public IMasterProductCatalogueStore DeniedMasterProductStore() =>
        new EfMasterProductCatalogueStore(Track(TestAppDbContext.Denied(_options)));

    /// <summary>
    /// The business lookup of the master products as a <strong>member's</strong> request reaches it
    /// (issue #1049): the real adapter on a context scoped to one business, which is the scope every
    /// <c>GET api/products/master-product-suggestions</c> request has.
    ///
    /// <para>That is the shape that matters here, and it is the opposite of the two stores above: this
    /// read is made by a member of a business, under their own resolved scope, and it must answer the
    /// shared catalogue anyway - because a master product carries no business key and so no central
    /// filter narrows it. It still reaches no mapping, no product and no business, which is what
    /// <see cref="UnscopedSuggestionStore"/> beside it is for.</para>
    /// </summary>
    public IMasterProductSuggestionStore SuggestionStoreFor(int businessId) =>
        new EfMasterProductSuggestionStore(Track(TestAppDbContext.For(_options, businessId)));

    /// <summary>
    /// The same store over a context with no filter at all. Setup-shaped, kept so a test can state
    /// that a member's own scoped read answers exactly what a completely unfiltered one does: what
    /// bounds this read is its reviewed field set and the endpoint's authorization, not the scope the
    /// request carries.
    /// </summary>
    public IMasterProductSuggestionStore UnscopedSuggestionStore() =>
        new EfMasterProductSuggestionStore(Track(TestAppDbContext.Unrestricted(_options)));

    /// <summary>
    /// The master-product write store as a platform-admin request reaches it (issue #1024): the real
    /// adapter on a context whose caller has no business at all, which is the scope every
    /// <c>api/super-admin/master-products</c> request has.
    ///
    /// <para>A new context each time, for the reason the collector and review stores get one: every
    /// create and every edit is its own request, and a reused change tracker would assert against
    /// entities the previous call left behind rather than against what was stored.</para>
    ///
    /// <para>The denied scope is also what proves the write needs no reviewed lease: it writes one
    /// platform table that carries no business key, so <c>BusinessOwnershipEnforcer</c> stays fully in
    /// force over everything tenant-owned and the write passes anyway.</para>
    /// </summary>
    public IMasterProductCatalogueWriteStore DeniedMasterProductWriteStore() =>
        new EfMasterProductCatalogueWriteStore(Track(TestAppDbContext.Denied(_options)));

    /// <summary>
    /// The same store on a context scoped to <em>one business</em>, for the test that says a
    /// catalogue write is a platform write: it must behave identically there, because what bounds it
    /// is authorization rather than a scope, and it must still not reach a tenant-owned row.
    /// </summary>
    public IMasterProductCatalogueWriteStore MasterProductWriteStoreFor(int businessId) =>
        new EfMasterProductCatalogueWriteStore(Track(TestAppDbContext.For(_options, businessId)));

    /// <summary>One master product by its key, from outside the boundary, for verifying a write.</summary>
    public async Task<MasterProduct?> MasterProductAsync(long masterProductId)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        return await db.MasterProducts
            .AsNoTracking()
            .SingleOrDefaultAsync(master => master.Id == masterProductId);
    }

    /// <summary>
    /// Holds one reading against a listing, as the collector results of #937 do when a reading is not
    /// a price: written directly, because what is under test here is what the <em>review</em> does
    /// with a held reading.
    /// </summary>
    public async Task<long> SeedPendingObservationAsync(
        long supplierListingId,
        long priceCollectorRunId,
        long priceCollectorWorkItemId,
        decimal packPrice,
        DateTime observedAtUtc,
        string reason = PriceCollectorResultPolicy.ListingNotConfirmedReason,
        string? variant = "Classic",
        decimal? sizeValue = 375m,
        MasterProductSizeUnit? sizeUnit = MasterProductSizeUnit.Millilitre,
        string? barcode = "0012345678905",
        BarcodeScope? barcodeScope = BarcodeScope.Unit,
        int? unitsPerPack = 30,
        SupplierPriceBasis priceBasis = SupplierPriceBasis.InclusiveOfGst,
        decimal? supplierGstRate = 0.1000m)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        var pending = new SupplierListingPendingObservation
        {
            SupplierListingId = supplierListingId,
            PriceCollectorRunId = priceCollectorRunId,
            PriceCollectorWorkItemId = priceCollectorWorkItemId,
            Reason = reason,
            PackPrice = packPrice,
            Currency = SupplierPriceObservationPolicy.Currency,
            PriceBasis = priceBasis,
            SupplierGstRate = supplierGstRate,
            StockStatus = SupplierStockStatus.InStock,
            ObservedAtUtc = observedAtUtc,
            Variant = variant,
            SizeValue = sizeValue,
            SizeUnit = sizeUnit,
            Barcode = barcode,
            BarcodeScope = barcodeScope,
            UnitsPerPack = unitsPerPack,
            Title = "Alpha Grocer cola",
            RecordedAtUtc = observedAtUtc,
        };

        db.SupplierListingPendingObservations.Add(pending);
        await db.SaveChangesAsync();

        return pending.Id;
    }

    /// <summary>
    /// Records which search originally found one listing, which is what an undo of an automatic match
    /// moves it back to (#923's provenance column). Written directly, because what put it there is a
    /// match the test has arranged rather than made.
    /// </summary>
    public async Task SetOriginWaitingKeyAsync(long listingId, string originWaitingKey)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        var listing = await db.SupplierListings.SingleAsync(candidate => candidate.Id == listingId);
        listing.OriginWaitingKey = originWaitingKey;

        await db.SaveChangesAsync();
    }

    /// <summary>Puts one listing into a match status directly, for a test about what it may be decided from.</summary>
    public async Task SetListingStatusAsync(long listingId, SupplierListingMatchStatus status)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        var listing = await db.SupplierListings.SingleAsync(candidate => candidate.Id == listingId);
        listing.MatchStatus = status;

        await db.SaveChangesAsync();
    }

    /// <summary>One match suggestion by its key, from outside the boundary, for verifying a decision.</summary>
    public async Task<ProductMatchSuggestion> SuggestionAsync(long suggestionId)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        return await db.ProductMatchSuggestions
            .AsNoTracking()
            .SingleAsync(suggestion => suggestion.Id == suggestionId);
    }

    /// <summary>
    /// Records a match suggestion against one waiting key and answers its key, so a test can decide
    /// that very row. The same write <see cref="SeedMatchSuggestionAsync"/> makes, with the id
    /// returned.
    /// </summary>
    public async Task<long> SeedSuggestionAsync(
        string waitingKey,
        long? masterProductId,
        ProductMatchSuggestionStatus status,
        decimal confidence = 0.9m,
        string collectorRun = "fixture",
        string? suggestedBrand = null,
        string? suggestedName = null,
        decimal? suggestedSizeValue = null,
        MasterProductSizeUnit? suggestedSizeUnit = null)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        var suggestion = new ProductMatchSuggestion
        {
            WaitingKey = waitingKey,
            MasterProductId = masterProductId,
            SuggestedBrand = suggestedBrand,
            SuggestedName = suggestedName,
            SuggestedSizeValue = suggestedSizeValue,
            SuggestedSizeUnit = suggestedSizeUnit,
            Confidence = confidence,
            Reason = "Seeded by the fixture.",
            CollectorRun = collectorRun,
            Status = status,
            CreatedAtUtc = CreatedAt,
            UpdatedAtUtc = CreatedAt,
        };

        db.ProductMatchSuggestions.Add(suggestion);
        await db.SaveChangesAsync();

        return suggestion.Id;
    }

    /// <summary>
    /// Every price-tracking mapping of both businesses, from outside the boundary, for verifying what
    /// a match did and did not change.
    /// </summary>
    public async Task<IReadOnlyList<ProductPriceTracking>> AllTrackingsAsync()
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        return await db.ProductPriceTrackings
            .AsNoTracking()
            .OrderBy(tracking => tracking.Id)
            .ToListAsync();
    }

    /// <summary>One collector run's status, on the same terms: an expiry is a write like any other.</summary>
    public async Task<PriceCollectorRunStatus> RunStatusAsync(long runId)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        return await db.PriceCollectorRuns
            .AsNoTracking()
            .Where(run => run.Id == runId)
            .Select(run => run.Status)
            .SingleAsync();
    }

    /// <summary>Every master product, on the same terms: a match may never create one.</summary>
    public async Task<IReadOnlyList<MasterProduct>> AllMasterProductsAsync()
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        return await db.MasterProducts.AsNoTracking().OrderBy(master => master.Id).ToListAsync();
    }

    /// <summary>
    /// One listing with the identity issue #937 checks a submitted row against, in whatever match
    /// status the test is about. Written directly, because the only writer of a confirmed or
    /// refused listing is #928 and that is out of scope here.
    /// </summary>
    public async Task<long> SeedIdentifiedListingAsync(
        long? masterProductId,
        string? waitingKey,
        string supplierProductId,
        SupplierListingMatchStatus matchStatus,
        string? variant = "Classic",
        decimal? sizeValue = 375m,
        MasterProductSizeUnit? sizeUnit = MasterProductSizeUnit.Millilitre,
        string? barcode = "0012345678905",
        BarcodeScope? barcodeScope = BarcodeScope.Unit,
        int? unitsPerPack = 30,
        int? supplierPresetId = null)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        var listing = new SupplierListing
        {
            SupplierPresetId = supplierPresetId ?? SupplierPresetId,
            MasterProductId = masterProductId,
            WaitingKey = waitingKey,
            OriginWaitingKey = waitingKey,
            SupplierProductId = supplierProductId,
            Variant = variant,
            SizeValue = sizeValue,
            SizeUnit = sizeUnit,
            Barcode = barcode,
            BarcodeScope = barcodeScope,
            UnitsPerPack = unitsPerPack,
            MatchStatus = matchStatus,
            CreatedAtUtc = CreatedAt,
            UpdatedAtUtc = CreatedAt,
        };

        db.SupplierListings.Add(listing);
        await db.SaveChangesAsync();

        return listing.Id;
    }

    /// <summary>
    /// Gives one listing a last accepted price, so the band of issue #937 has something to be a
    /// band around. Written directly for the same reason: the only writer is the endpoint under
    /// test, and a test about the band must not depend on it to arrange the state.
    /// </summary>
    public async Task SeedAcceptedPriceAsync(
        long supplierListingId,
        long priceCollectorRunId,
        decimal packPrice,
        DateTime observedAtUtc,
        SupplierPriceBasis priceBasis = SupplierPriceBasis.InclusiveOfGst)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        var observation = new SupplierPriceObservation
        {
            SupplierListingId = supplierListingId,
            PriceCollectorRunId = priceCollectorRunId,
            PackPrice = packPrice,
            Currency = SupplierPriceObservationPolicy.Currency,
            PriceBasis = priceBasis,
            StockStatus = SupplierStockStatus.InStock,
            ObservedAtUtc = observedAtUtc,
            CreatedAtUtc = observedAtUtc,
        };

        db.SupplierPriceObservations.Add(observation);
        await db.SaveChangesAsync();

        var listing = await db.SupplierListings.SingleAsync(row => row.Id == supplierListingId);
        listing.LatestObservationId = observation.Id;
        listing.LastCheckedAtUtc = observedAtUtc;
        listing.LastPriceChangeAtUtc = observedAtUtc;

        await db.SaveChangesAsync();
    }

    /// <summary>One work item inserted directly, answering the id a result row must cite.</summary>
    public async Task<long> SeedWorkItemAsync(
        long priceCollectorRunId,
        long? masterProductId,
        string? waitingKey,
        int? supplierPresetId = null)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        var workItem = new PriceCollectorWorkItem
        {
            PriceCollectorRunId = priceCollectorRunId,
            SupplierPresetId = supplierPresetId ?? SupplierPresetId,
            MasterProductId = masterProductId,
            WaitingKey = waitingKey,
            IssuedAtUtc = CreatedAt,
        };

        db.PriceCollectorWorkItems.Add(workItem);
        await db.SaveChangesAsync();

        return workItem.Id;
    }

    /// <summary>One open run with an expiry the test chooses, for the expiry behaviour of #937.</summary>
    public async Task<long> SeedRunAsync(DateTime startedAtUtc, DateTime? expiresAtUtc)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        var run = new PriceCollectorRun
        {
            StartedAtUtc = startedAtUtc,
            ExpiresAtUtc = expiresAtUtc,
            Status = PriceCollectorRunStatus.Running,
        };

        db.PriceCollectorRuns.Add(run);
        await db.SaveChangesAsync();

        return run.Id;
    }

    /// <summary>Every supplier listing, from outside the boundary, for verifying a write.</summary>
    public async Task<IReadOnlyList<SupplierListing>> AllListingsAsync()
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        return await db.SupplierListings.AsNoTracking().OrderBy(row => row.Id).ToListAsync();
    }

    /// <summary>One supplier listing by its key, on the same terms.</summary>
    public async Task<SupplierListing> ListingAsync(long supplierListingId)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        return await db.SupplierListings.AsNoTracking().SingleAsync(row => row.Id == supplierListingId);
    }

    /// <summary>Every accepted observation, on the same terms.</summary>
    public async Task<IReadOnlyList<SupplierPriceObservation>> AllObservationsAsync()
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        return await db.SupplierPriceObservations.AsNoTracking().OrderBy(row => row.Id).ToListAsync();
    }

    /// <summary>Every held reading, on the same terms.</summary>
    public async Task<IReadOnlyList<SupplierListingPendingObservation>> AllPendingObservationsAsync()
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        return await db.SupplierListingPendingObservations
            .AsNoTracking()
            .OrderBy(row => row.Id)
            .ToListAsync();
    }

    /// <summary>Every recorded result row, on the same terms.</summary>
    public async Task<IReadOnlyList<PriceCollectorResultRow>> AllResultRowsAsync()
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        return await db.PriceCollectorResultRows.AsNoTracking().OrderBy(row => row.Id).ToListAsync();
    }

    /// <summary>Every match suggestion, on the same terms.</summary>
    public async Task<IReadOnlyList<ProductMatchSuggestion>> AllSuggestionsAsync()
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        return await db.ProductMatchSuggestions.AsNoTracking().OrderBy(row => row.Id).ToListAsync();
    }

    /// <summary>
    /// A fingerprint of everything either business owns that a price could plausibly touch, so a
    /// test can state that a collector submission wrote none of it. Products, purchases, purchase
    /// lines, suppliers, stock adjustments and the businesses themselves, counted and summed.
    /// </summary>
    public async Task<string> TenantOwnedFingerprintAsync()
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        var products = await db.Products
            .AsNoTracking()
            .OrderBy(product => product.Id)
            .Select(product => new
            {
                product.Id,
                product.BusinessId,
                product.Name,
                product.Unit,
                product.QuantityInStock,
                product.AverageUnitCost,
                product.InventoryValue,
                product.MachinePrice,
            })
            .ToListAsync();

        var purchases = await db.Receipts.AsNoTracking().CountAsync();
        var purchaseItems = await db.ReceiptItems.AsNoTracking().CountAsync();
        var suppliers = await db.Suppliers.AsNoTracking().CountAsync();
        var adjustments = await db.StockAdjustments.AsNoTracking().CountAsync();
        var trackings = await db.ProductPriceTrackings
            .AsNoTracking()
            .OrderBy(tracking => tracking.Id)
            .Select(tracking => new
            {
                tracking.Id,
                tracking.BusinessId,
                tracking.ProductId,
                tracking.Status,
                tracking.MasterProductId,
                tracking.WaitingKey,
                tracking.UpdatedAtUtc,
            })
            .ToListAsync();

        return System.Text.Json.JsonSerializer.Serialize(
            new { products, purchases, purchaseItems, suppliers, adjustments, trackings });
    }

    /// <summary>
    /// Records the grandfathering step as completed (issue #966), which is the second thing plan
    /// enforcement waits for: with the rollout setting on and no completion marker, every business
    /// is still unrestricted.
    /// </summary>
    public Task CompleteGrandfatheringAsync() => WriteGrandfatheringAsync(completed: true);

    /// <summary>The interrupted rollout: a cutoff is recorded and the step never finished.</summary>
    public Task StartGrandfatheringAsync() => WriteGrandfatheringAsync(completed: false);

    /// <summary>
    /// Puts one business on <paramref name="plan"/> through a manual grant (issue #966), which is
    /// how the grandfathered and complimentary plans a business holds are recorded.
    ///
    /// <para>Free is deliberately not written as a grant: it is the <em>absence</em> of an active
    /// record, which is how a business really arrives there, so a test puts a business on Free by
    /// giving it no grant at all or by letting the one it has end.</para>
    /// </summary>
    public async Task GrantPlanAsync(
        int businessId,
        SubscriptionPlan plan,
        DateTime? startsAtUtc = null,
        DateTime? endsAtUtc = null)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        db.BusinessPlanGrants.Add(new BusinessPlanGrant
        {
            BusinessId = businessId,
            Plan = plan,
            Source = ManualPlanGrantSource.Complimentary,
            StartsAtUtc = startsAtUtc ?? GrantedFrom,
            EndsAtUtc = endsAtUtc,
            Reason = "Fixture",
            GrantedAtUtc = startsAtUtc ?? GrantedFrom,
            Revision = 1,
        });

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Puts one business on <paramref name="plan"/> through a paid subscription instead, so the
    /// collector's eligibility can be shown to read the same entitlement rule as everything else
    /// rather than only the grants.
    /// </summary>
    public async Task SubscribeAsync(
        int businessId,
        SubscriptionPlan plan,
        BillingSubscriptionStatus status = BillingSubscriptionStatus.Active)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        db.BusinessBillingSubscriptions.Add(new BusinessBillingSubscription
        {
            BusinessId = businessId,
            Plan = plan,
            Status = status,
            CurrentPeriodStartUtc = GrantedFrom,
            CurrentPeriodEndUtc = null,
            UpdatedAtUtc = GrantedFrom,
        });

        await db.SaveChangesAsync();
    }

    private async Task WriteGrandfatheringAsync(bool completed)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        db.SubscriptionGrandfatherings.Add(new SubscriptionGrandfathering
        {
            Id = SubscriptionGrandfathering.SingletonId,
            CutoffUtc = GrantedFrom,
            CompletedAtUtc = completed ? GrantedFrom.AddMinutes(5) : null,
            GrantedBusinessCount = completed ? 1 : 0,
        });

        await db.SaveChangesAsync();
    }

    public async Task<int> SeedBusinessAsync(string name)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        var business = new Business
        {
            Name = name,
            TimeZoneId = "Australia/Sydney",
            Status = BusinessStatus.Active,
            CreatedAtUtc = CreatedAt,
        };

        db.Businesses.Add(business);
        await db.SaveChangesAsync();

        return business.Id;
    }

    public async Task<long> SeedProductAsync(int businessId, string name, string? unit)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        var product = new Product
        {
            BusinessId = businessId,
            Name = name,
            Unit = unit,
            CreatedAt = CreatedAt,
            UpdatedAt = CreatedAt,
        };

        db.Products.Add(product);
        await db.SaveChangesAsync();

        return product.Id;
    }

    public async Task<int> SeedSupplierPresetAsync(string name)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        var preset = new SupplierPreset
        {
            Name = name,
            NormalisedName = SupplierPresetPolicy.NormalisedName(name),
            IsEnabled = true,
            ServesNationwide = true,
            CreatedAtUtc = CreatedAt,
            UpdatedAtUtc = CreatedAt,
        };

        db.SupplierPresets.Add(preset);
        await db.SaveChangesAsync();

        return preset.Id;
    }

    public async Task<long> SeedMasterProductAsync(
        string brand,
        string name,
        decimal size = 375m,
        MasterProductSizeUnit unit = MasterProductSizeUnit.Millilitre)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        var master = new MasterProduct
        {
            Brand = brand,
            Name = name,
            SizeValue = size,
            SizeUnit = unit,
            NormalisedKey = MasterProductPolicy.NormalisedKey(brand, name, size, unit),
            CreatedAtUtc = CreatedAt,
            UpdatedAtUtc = CreatedAt,
        };

        db.MasterProducts.Add(master);
        await db.SaveChangesAsync();

        return master.Id;
    }

    public async Task<long> SeedListingAsync(
        long masterProductId,
        string supplierProductId,
        int? supplierPresetId = null,
        int? unitsPerPack = null,
        string? barcode = null,
        BarcodeScope? barcodeScope = null)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        var listing = new SupplierListing
        {
            SupplierPresetId = supplierPresetId ?? SupplierPresetId,
            MasterProductId = masterProductId,
            SupplierProductId = supplierProductId,
            UnitsPerPack = unitsPerPack,
            Barcode = barcode,
            BarcodeScope = barcodeScope,
            MatchStatus = SupplierListingMatchStatus.Proposed,
            CreatedAtUtc = CreatedAt,
            UpdatedAtUtc = CreatedAt,
        };

        db.SupplierListings.Add(listing);
        await db.SaveChangesAsync();

        return listing.Id;
    }

    /// <summary>
    /// One listing of a master product described the way a supplier publishes it (issue #1023): the
    /// supplier's own wording and page, the individual size, the pack quantity and the match status a
    /// test is about.
    ///
    /// <para>Separate from <see cref="SeedListingAsync"/> because the catalogue detail publishes the
    /// title and the URL, which that one does not set, and from
    /// <see cref="SeedIdentifiedListingAsync"/> because the identity fields #937 compares are not what
    /// is under test here.</para>
    /// </summary>
    public async Task<long> SeedDescribedListingAsync(
        long masterProductId,
        string supplierProductId,
        SupplierListingMatchStatus matchStatus = SupplierListingMatchStatus.Proposed,
        string? title = null,
        string? url = null,
        decimal? sizeValue = null,
        MasterProductSizeUnit? sizeUnit = null,
        int? unitsPerPack = null,
        int? supplierPresetId = null)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        var listing = new SupplierListing
        {
            SupplierPresetId = supplierPresetId ?? SupplierPresetId,
            MasterProductId = masterProductId,
            SupplierProductId = supplierProductId,
            Title = title,
            Url = url,
            SizeValue = sizeValue,
            SizeUnit = sizeUnit,
            UnitsPerPack = unitsPerPack,
            MatchStatus = matchStatus,
            CreatedAtUtc = CreatedAt,
            UpdatedAtUtc = CreatedAt,
        };

        db.SupplierListings.Add(listing);
        await db.SaveChangesAsync();

        return listing.Id;
    }

    /// <summary>
    /// Several listings of one master product in one save, for the per-product size bounds of issue
    /// #924: the bound is about how <em>many</em> there are, so what a test needs is a number of them
    /// rather than a described one. Each gets its own supplier product id, because the pair
    /// <c>(supplier master record, supplier product id)</c> is unique.
    /// </summary>
    public async Task SeedListingsAsync(long masterProductId, int count)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        for (var index = 0; index < count; index++)
        {
            db.SupplierListings.Add(new SupplierListing
            {
                SupplierPresetId = SupplierPresetId,
                MasterProductId = masterProductId,
                SupplierProductId = $"SKU-{masterProductId}-{index:D4}",
                MatchStatus = SupplierListingMatchStatus.Proposed,
                CreatedAtUtc = CreatedAt,
                UpdatedAtUtc = CreatedAt,
            });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Several refused master products against one waiting key in one save, on the same terms: a
    /// master product per refusal, because a refusal names one.
    /// </summary>
    public async Task SeedRefusalsAsync(string waitingKey, int count)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        for (var index = 0; index < count; index++)
        {
            var brand = $"Refused {waitingKey.GetHashCode(StringComparison.Ordinal):X8}";
            var name = $"Variant {index:D4}";

            var master = new MasterProduct
            {
                Brand = brand,
                Name = name,
                SizeValue = 375m,
                SizeUnit = MasterProductSizeUnit.Millilitre,
                NormalisedKey = MasterProductPolicy.NormalisedKey(
                    brand, name, 375m, MasterProductSizeUnit.Millilitre),
                CreatedAtUtc = CreatedAt,
                UpdatedAtUtc = CreatedAt,
            };

            db.MasterProducts.Add(master);
            db.ProductMatchSuggestions.Add(new ProductMatchSuggestion
            {
                WaitingKey = waitingKey,
                MasterProduct = master,
                Confidence = 0.9m,
                Reason = "Seeded by the fixture.",
                CollectorRun = "fixture",
                Status = ProductMatchSuggestionStatus.Rejected,
                CreatedAtUtc = CreatedAt,
                UpdatedAtUtc = CreatedAt,
            });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>A listing that is still held under a waiting key, with no master product yet.</summary>
    public async Task SeedWaitingListingAsync(string waitingKey, string supplierProductId)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        db.SupplierListings.Add(new SupplierListing
        {
            SupplierPresetId = SupplierPresetId,
            WaitingKey = waitingKey,
            OriginWaitingKey = waitingKey,
            SupplierProductId = supplierProductId,
            MatchStatus = SupplierListingMatchStatus.Proposed,
            CreatedAtUtc = CreatedAt,
            UpdatedAtUtc = CreatedAt,
        });

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Turns tracking on for one product, through the real adapter on that business's own scope, and
    /// answers the mapping it stored.
    ///
    /// <para>No name or unit label is passed: since issue #938 the adapter reads the product's own
    /// name and unit label inside the write, which is exactly the property most of these tests are
    /// about - the search text is the product's, never text a caller captured.</para>
    /// </summary>
    public async Task<ProductPriceTrackingRecord> TrackAsync(int businessId, long productId)
    {
        var store = TrackingStoreFor(businessId);

        var write = await store.TrackAsync(productId, CreatedAt, CancellationToken.None);
        Assert.Equal(ProductPriceTrackingWriteOutcome.Tracked, write.Outcome);

        var stored = await store.FindForProductAsync(productId, CancellationToken.None);
        return stored!.Tracking!;
    }

    /// <summary>
    /// The same write without asserting its outcome, for the tests that are about a refusal or a
    /// second attempt.
    /// </summary>
    public Task<ProductPriceTrackingWrite> TryTrackAsync(int businessId, long productId) =>
        TrackingStoreFor(businessId).TrackAsync(productId, CreatedAt, CancellationToken.None);

    /// <summary>Turns tracking off for one product, through the real adapter on that business's scope.</summary>
    public Task<ProductPriceTrackingWrite> UntrackAsync(int businessId, long productId) =>
        TrackingStoreFor(businessId).UntrackAsync(productId, CancellationToken.None);

    /// <summary>
    /// Edits a product's name and unit label through the <strong>real</strong> product store on that
    /// business's own scope, which is what makes the mapping refresh of issue #938 under test: the
    /// refresh happens inside that store's own update transaction, so arranging the edit by writing
    /// the row directly would prove nothing.
    ///
    /// <para>Everything else on the product is sent as the fixture's seeded products hold it - no SKU,
    /// no description, no category - because what is under test is the name and the label.</para>
    /// </summary>
    public async Task<ProductWriteAttempt> UpdateProductAsync(
        int businessId,
        long productId,
        string name,
        string? unit)
    {
        await using var db = TestAppDbContext.For(_options, businessId);

        var store = new EfProductStore(db, TestCostingUseCases.Rebuild(db));

        return await store.UpdateAsync(
            productId,
            new ProductUpdateFields(name, null, null, 0, 0, unit, null, true),
            CancellationToken.None);
    }

    /// <summary>
    /// Records a decided match on an existing mapping, which no port method does yet: deciding one is
    /// the matcher of #941 and the review of #928, both out of this issue's scope, so the state is
    /// arranged directly and only the reads are under test.
    /// </summary>
    public async Task MatchAsync(
        long trackingId,
        long masterProductId,
        ProductPriceTrackingStatus status = ProductPriceTrackingStatus.Matched,
        ProductPriceTrackingMatchSource source = ProductPriceTrackingMatchSource.SuperAdmin)
    {
        ProductPriceTrackingPolicy.RequireConsistentMatch(status, masterProductId, source);

        await using var db = TestAppDbContext.Unrestricted(_options);

        var row = await db.ProductPriceTrackings.SingleAsync(tracking => tracking.Id == trackingId);
        row.MasterProductId = masterProductId;
        row.Status = status;
        row.MatchSource = source;
        row.UpdatedAtUtc = CreatedAt;

        await db.SaveChangesAsync();
    }

    /// <summary>Records "looked for, and not found", which is an answer rather than a gap.</summary>
    public async Task MarkNotFoundAsync(long trackingId)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        var row = await db.ProductPriceTrackings.SingleAsync(tracking => tracking.Id == trackingId);
        row.Status = ProductPriceTrackingStatus.NotFound;
        row.UpdatedAtUtc = CreatedAt;

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The same fixture with <strong>no supplier the collector could search</strong>: every shipped
    /// supplier master record's collection route is cleared, so the only due suppliers in a test are
    /// the ones that test configures itself.
    ///
    /// <para>The nine shipped presets arrive with the migrations and most of them carry a real
    /// connector, which is right for production and useless for a test about which suppliers are due:
    /// every assertion would be about rows a migration chose. Clearing the route is the smallest way
    /// to say "this test owns the supplier list", and it is a write to platform configuration rather
    /// than to anybody's data.</para>
    /// </summary>
    public static async Task<PriceCatalogueFixture> CreateWithNoCollectableSuppliersAsync()
    {
        var fixture = await CreateAsync();

        await using var db = TestAppDbContext.Unrestricted(fixture._options);

        foreach (var preset in await db.SupplierPresets.ToListAsync())
        {
            preset.PriceConnector = null;
            preset.PriceConnectorKey = null;
        }

        await db.SaveChangesAsync();

        return fixture;
    }

    /// <summary>
    /// Records how one supplier master record's prices are collected (issue #931's fields), which is
    /// what makes it due on a collector run at all. No port writes these - the Super Admin's preset
    /// edit does, and it is out of #924's scope - so the state is arranged directly and only the reads
    /// are under test.
    /// </summary>
    public async Task SetSupplierCollectionAsync(
        int supplierPresetId,
        SupplierPriceConnector? connector,
        string? connectorKey = null,
        string? url = null,
        int freshnessHours = SupplierPriceFreshnessPolicy.DailyCheckLimitHours,
        bool isEnabled = true)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        var preset = await db.SupplierPresets.SingleAsync(row => row.Id == supplierPresetId);
        preset.PriceConnector = connector;
        preset.PriceConnectorKey = connectorKey;
        preset.Url = url;
        preset.PriceCheckFreshnessHours = freshnessHours;
        preset.IsEnabled = isEnabled;
        preset.UpdatedAtUtc = CreatedAt;

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Records a match suggestion against one waiting key, which no port writes either: the matcher of
    /// #941 and the review of #928 do, and both are out of #924's scope.
    /// </summary>
    public async Task SeedMatchSuggestionAsync(
        string waitingKey,
        long masterProductId,
        ProductMatchSuggestionStatus status)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        db.ProductMatchSuggestions.Add(new ProductMatchSuggestion
        {
            WaitingKey = waitingKey,
            MasterProductId = masterProductId,
            Confidence = 0.9m,
            Reason = "Seeded by the fixture.",
            CollectorRun = "fixture",
            Status = status,
            CreatedAtUtc = CreatedAt,
            UpdatedAtUtc = CreatedAt,
        });

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Records one open collector run directly, for the constraint tests: the run store issues one
    /// with its whole work list, and what those tests are about is a single row the database refuses.
    /// </summary>
    public async Task<long> SeedRunAsync(DateTime startedAtUtc)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        var run = new PriceCollectorRun
        {
            StartedAtUtc = startedAtUtc,
            ExpiresAtUtc = PriceCollectorRunPolicy.ExpiresAt(
                startedAtUtc, PriceCollectorRunPolicy.DefaultExpiryHours),
            Status = PriceCollectorRunStatus.Running,
        };

        db.PriceCollectorRuns.Add(run);
        await db.SaveChangesAsync();

        return run.Id;
    }

    /// <summary>
    /// Inserts one work item directly, so the check constraints and the unique index are what refuse
    /// a row rather than the use case that would never have built it.
    /// </summary>
    public async Task InsertWorkItemAsync(
        long priceCollectorRunId,
        int supplierPresetId,
        long? masterProductId,
        string? waitingKey,
        PriceCollectorWorkItemOutcome? outcome = null,
        DateTime? outcomeAtUtc = null)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        db.PriceCollectorWorkItems.Add(new PriceCollectorWorkItem
        {
            PriceCollectorRunId = priceCollectorRunId,
            SupplierPresetId = supplierPresetId,
            MasterProductId = masterProductId,
            WaitingKey = waitingKey,
            IssuedAtUtc = CreatedAt,
            Outcome = outcome,
            OutcomeAtUtc = outcomeAtUtc,
        });

        await db.SaveChangesAsync();
    }

    /// <summary>Deletes one collector run, so a restricted reference can be proved to refuse it.</summary>
    public async Task DeleteRunAsync(long priceCollectorRunId)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        db.PriceCollectorRuns.Remove(
            await db.PriceCollectorRuns.SingleAsync(run => run.Id == priceCollectorRunId));

        await db.SaveChangesAsync();
    }

    /// <summary>Every column of one table, read from the schema rather than from the model.</summary>
    public Task<List<string>> ColumnNamesAsync(string table) =>
        MigrationSchemaProbe.ColumnNamesAsync(_connection, table);

    /// <summary>Every collector run in the database, from outside the boundary, for verifying a write.</summary>
    public async Task<IReadOnlyList<PriceCollectorRun>> AllRunsAsync()
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        return await db.PriceCollectorRuns.AsNoTracking().OrderBy(run => run.Id).ToListAsync();
    }

    /// <summary>Every issued work item in the database, on the same terms.</summary>
    public async Task<IReadOnlyList<PriceCollectorWorkItem>> AllWorkItemsAsync()
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        return await db.PriceCollectorWorkItems.AsNoTracking().OrderBy(item => item.Id).ToListAsync();
    }

    /// <summary>Every mapping in the database, from outside the boundary, for verifying a write.</summary>
    public async Task<IReadOnlyList<ProductPriceTracking>> AllMappingsAsync()
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        return await db.ProductPriceTrackings
            .AsNoTracking()
            .OrderBy(tracking => tracking.Id)
            .ToListAsync();
    }

    public async Task DeleteProductAsync(int businessId, long productId)
    {
        await using var db = TestAppDbContext.For(_options, businessId);

        db.Products.Remove(await db.Products.SingleAsync(product => product.Id == productId));

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Inserts a mapping behind the adapter's back, for the tests that are about the unique index
    /// itself: a row written by a writer the adapter's own in-lock read could not have seen.
    /// </summary>
    public async Task InsertMappingDirectlyAsync(int businessId, long productId)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        var product = await db.Products.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == productId);
        var captured = ProductPriceTrackingPolicy.Capture(product.Name, product.Unit);

        db.ProductPriceTrackings.Add(new ProductPriceTracking
        {
            BusinessId = businessId,
            ProductId = productId,
            Status = ProductPriceTrackingStatus.Requested,
            WaitingKey = captured.WaitingKey,
            SearchName = captured.SearchName,
            UnitLabel = captured.UnitLabel,
            CreatedAtUtc = CreatedAt,
            UpdatedAtUtc = CreatedAt,
        });

        await db.SaveChangesAsync();
    }

    /// <summary>Whether that master product is still there, for the "nothing shared was touched" tests.</summary>
    public async Task<bool> MasterProductExistsAsync(long masterProductId)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        return await db.MasterProducts.AnyAsync(master => master.Id == masterProductId);
    }

    /// <summary>Whether that supplier listing is still there, for the same reason.</summary>
    public async Task<bool> ListingExistsAsync(long listingId)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        return await db.SupplierListings.AnyAsync(listing => listing.Id == listingId);
    }

    public async Task DeleteMasterProductAsync(long masterProductId)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        db.MasterProducts.Remove(
            await db.MasterProducts.SingleAsync(master => master.Id == masterProductId));

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The cheapest-supplier summary store as a request would reach it (issue #930): the real
    /// adapter on a context scoped to one business, which is the only way the tenant filters are
    /// under test at all.
    /// </summary>
    public ICheapestSupplierSummaryStore SummaryStoreFor(int businessId) =>
        new EfCheapestSupplierSummaryStore(Track(TestAppDbContext.For(_options, businessId)));

    /// <summary>The same store for a caller whose business could not be resolved.</summary>
    public ICheapestSupplierSummaryStore DeniedSummaryStore() =>
        new EfCheapestSupplierSummaryStore(Track(TestAppDbContext.Denied(_options)));

    /// <summary>Records whether a business is registered for GST, which decides its price basis.</summary>
    public async Task SetGstRegisteredAsync(int businessId, bool? gstRegistered)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        var business = await db.Businesses.SingleAsync(candidate => candidate.Id == businessId);
        business.GstRegistered = gstRegistered;

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// One of a business's own suppliers, optionally a linked copy of a platform record (issue
    /// #934) and optionally switched out of use. Written directly because what is under test is
    /// which of them the summary reads, not how a copy is made.
    /// </summary>
    public async Task<int> SeedSupplierAsync(
        int businessId,
        string name,
        int? supplierPresetId = null,
        bool hidden = false)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        var supplier = new Supplier
        {
            BusinessId = businessId,
            Name = name,
            SupplierPresetId = supplierPresetId,
            IsHidden = hidden,
        };

        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();

        return supplier.Id;
    }

    /// <summary>A collector run to hang observations from, because provenance is required.</summary>
    public async Task<long> SeedCollectorRunAsync()
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        var run = new PriceCollectorRun
        {
            StartedAtUtc = CreatedAt,
            CompletedAtUtc = CreatedAt,
            Status = PriceCollectorRunStatus.Succeeded,
        };

        db.PriceCollectorRuns.Add(run);
        await db.SaveChangesAsync();

        return run.Id;
    }

    /// <summary>
    /// Records one observed price for a listing and points the listing at it, which is what
    /// <c>SupplierListingPriceStatePolicy</c> requires of a successful check: the cursor, the check
    /// instant and the price move together.
    /// </summary>
    public async Task<long> SeedObservationAsync(
        long listingId,
        long collectorRunId,
        decimal packPrice,
        SupplierPriceBasis basis = SupplierPriceBasis.InclusiveOfGst,
        decimal? supplierGstRate = 0.1000m,
        SupplierStockStatus stockStatus = SupplierStockStatus.InStock,
        DateTime? observedAtUtc = null,
        bool pointListingAtIt = true)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        var observation = new SupplierPriceObservation
        {
            SupplierListingId = listingId,
            PriceCollectorRunId = collectorRunId,
            PackPrice = packPrice,
            PriceBasis = basis,
            SupplierGstRate = supplierGstRate,
            StockStatus = stockStatus,
            ObservedAtUtc = observedAtUtc ?? CreatedAt,
            CreatedAtUtc = CreatedAt,
        };

        db.SupplierPriceObservations.Add(observation);
        await db.SaveChangesAsync();

        if (pointListingAtIt)
        {
            var listing = await db.SupplierListings.SingleAsync(candidate => candidate.Id == listingId);
            listing.LatestObservationId = observation.Id;
            listing.LastCheckedAtUtc = observation.ObservedAtUtc;
            listing.LastPriceChangeAtUtc = observation.ObservedAtUtc;
            await db.SaveChangesAsync();
        }

        return observation.Id;
    }

    /// <summary>Confirms a listing's match, which is what lets its price be compared at all.</summary>
    public async Task ConfirmListingAsync(long listingId)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        var listing = await db.SupplierListings.SingleAsync(candidate => candidate.Id == listingId);
        listing.MatchStatus = SupplierListingMatchStatus.Confirmed;

        await db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var context in _contexts)
        {
            await context.DisposeAsync();
        }

        await _connection.DisposeAsync();
    }

    private AppDbContext Track(AppDbContext context)
    {
        _contexts.Add(context);
        return context;
    }
}
