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

    /// <summary>The work-list store on a context scoped to one business.</summary>
    public IPriceCatalogueWorkListStore WorkListStoreFor(int businessId) =>
        new EfPriceCatalogueWorkListStore(Track(TestAppDbContext.For(_options, businessId)));

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
    /// </summary>
    public IPriceCatalogueWorkListStore DeniedWorkListStore() =>
        new EfPriceCatalogueWorkListStore(Track(TestAppDbContext.Denied(_options)));

    /// <summary>
    /// The work-list store over a context that sees every business's rows without any filter at all.
    /// Setup-shaped, kept so a test can state that the reviewed read answers the same thing a
    /// completely unfiltered one does - the read is bounded by the collector endpoint's
    /// authorization, not by the context it is given.
    /// </summary>
    public IPriceCatalogueWorkListStore UnscopedWorkListStore() =>
        new EfPriceCatalogueWorkListStore(Track(TestAppDbContext.Unrestricted(_options)));

    /// <summary>The collector run store as a collector request reaches it: a denied scope.</summary>
    public IPriceCollectorRunStore DeniedRunStore() =>
        new EfPriceCollectorRunStore(Track(TestAppDbContext.Denied(_options)));

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
