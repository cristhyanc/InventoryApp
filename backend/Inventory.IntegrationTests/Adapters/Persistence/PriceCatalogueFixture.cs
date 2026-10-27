using Inventory.Application.PriceCatalogue;
using Inventory.Domain.PriceCatalogue;
using Inventory.Domain.Suppliers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

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
    /// Turns tracking on for one product, through the real adapter on that business's own scope.
    /// </summary>
    public Task<ProductPriceTrackingRecord> TrackAsync(
        int businessId,
        long productId,
        string productName,
        string? productUnit) =>
        TrackingStoreFor(businessId).AddAsync(
            NewProductPriceTracking.For(productId, productName, productUnit, CreatedAt),
            CancellationToken.None);

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

    public async Task DeleteMasterProductAsync(long masterProductId)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        db.MasterProducts.Remove(
            await db.MasterProducts.SingleAsync(master => master.Id == masterProductId));

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
