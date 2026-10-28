using Inventory.Application.PriceCatalogue;
using Inventory.Application.Products;
using Inventory.Domain.PriceCatalogue;
using Inventory.Domain.Suppliers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
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

    /// <summary>The work-list store scoped to one business, which sees that business's mappings only.</summary>
    public IPriceCatalogueWorkListStore WorkListStoreFor(int businessId) =>
        new EfPriceCatalogueWorkListStore(Track(TestAppDbContext.For(_options, businessId)));

    /// <summary>
    /// The work-list store over a context that sees every business's mappings.
    ///
    /// <para>This is setup-shaped rather than request-shaped, deliberately and visibly: the adapter
    /// reads the mappings through the ordinary tenant filters and adds no cross-business read path of
    /// its own, so the deduplication <em>across</em> businesses - which is what issue #923's third and
    /// seventh criteria are about - is stated here over a context that already sees both. Which caller
    /// may read the work list that way, and how it authenticates, is #924's own reviewed decision; no
    /// request path in this application can do it today.</para>
    /// </summary>
    public IPriceCatalogueWorkListStore UnscopedWorkListStore() =>
        new EfPriceCatalogueWorkListStore(Track(TestAppDbContext.Unrestricted(_options)));

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
