using Inventory.Domain.PriceCatalogue;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Domain.Suppliers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The rules the shared supplier price catalogue's own schema enforces (issue #923, parent #817):
/// the exclusive ownership of a supplier listing, the pack, size and barcode rules, one proposal per
/// match suggestion, the uniqueness rules, and the references that restrict.
///
/// <para>Relational SQLite over the real migration history, because every property here is the
/// database's rather than the application's. A <c>CHECK</c> constraint and a <c>UNIQUE</c> index are
/// exactly the things an application-level check can be trusted to have skipped: AGENTS.md § Database
/// and migrations requires a relational test where constraints matter, and "a listing with both or
/// neither of a master product and a waiting key is refused" is a claim about what the database will
/// store, not about what a policy returns.</para>
/// </summary>
public class SupplierPriceCatalogueConstraintTests
{
    private static readonly DateTime RecordedAt = new(2026, 10, 27, 5, 0, 0, DateTimeKind.Utc);

    #region A listing belongs to exactly one owner

    /// <summary>
    /// The criterion itself: a listing references exactly one of a master product and an active
    /// waiting key. Both at once is a listing claiming to be two things, and the database refuses it.
    /// </summary>
    [Fact]
    public async Task A_listing_with_both_a_master_product_and_a_waiting_key_is_refused()
    {
        await using var fixture = await CatalogueFixture.CreateAsync();

        var refusal = await Assert.ThrowsAsync<DbUpdateException>(
            () => fixture.AddListingAsync(
                masterProductId: fixture.MasterProductId,
                waitingKey: fixture.WaitingKey,
                supplierProductId: "both"));

        Assert.IsType<SqliteException>(refusal.InnerException);
        Assert.Empty(await fixture.ListingsAsync());
    }

    /// <summary>And neither, which is a listing nothing could ever find again.</summary>
    [Fact]
    public async Task A_listing_with_neither_a_master_product_nor_a_waiting_key_is_refused()
    {
        await using var fixture = await CatalogueFixture.CreateAsync();

        await Assert.ThrowsAsync<DbUpdateException>(
            () => fixture.AddListingAsync(
                masterProductId: null,
                waitingKey: null,
                supplierProductId: "neither"));

        Assert.Empty(await fixture.ListingsAsync());
    }

    [Fact]
    public async Task A_listing_held_under_a_waiting_key_alone_is_stored()
    {
        await using var fixture = await CatalogueFixture.CreateAsync();

        await fixture.AddListingAsync(
            masterProductId: null,
            waitingKey: fixture.WaitingKey,
            supplierProductId: "waiting");

        var listing = Assert.Single(await fixture.ListingsAsync());
        Assert.Null(listing.MasterProductId);
        Assert.Equal(fixture.WaitingKey, listing.WaitingKey);
    }

    /// <summary>
    /// The other half of the criterion: the origin waiting key survives the move to a master product.
    /// It is a separate column precisely so that discovery provenance and current ownership do not
    /// have to be the same value - a matched listing can still say which search found it without
    /// violating the exclusive rule above.
    /// </summary>
    [Fact]
    public async Task A_listing_moved_to_a_master_product_keeps_the_waiting_key_it_was_discovered_under()
    {
        await using var fixture = await CatalogueFixture.CreateAsync();

        await fixture.AddListingAsync(
            masterProductId: fixture.MasterProductId,
            waitingKey: null,
            supplierProductId: "matched",
            originWaitingKey: fixture.WaitingKey);

        var listing = Assert.Single(await fixture.ListingsAsync());
        Assert.Equal(fixture.MasterProductId, listing.MasterProductId);
        Assert.Null(listing.WaitingKey);
        Assert.Equal(fixture.WaitingKey, listing.OriginWaitingKey);
    }

    #endregion

    #region Pack, size and barcode

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_pack_size_that_is_not_positive_is_refused_by_the_database(int unitsPerPack)
    {
        await using var fixture = await CatalogueFixture.CreateAsync();

        await Assert.ThrowsAsync<DbUpdateException>(
            () => fixture.AddListingAsync(
                masterProductId: fixture.MasterProductId,
                waitingKey: null,
                supplierProductId: "pack",
                unitsPerPack: unitsPerPack));

        Assert.Empty(await fixture.ListingsAsync());
    }

    /// <summary>Unknown stays unknown, and is stored as unknown.</summary>
    [Fact]
    public async Task An_unknown_pack_size_is_stored_as_unknown()
    {
        await using var fixture = await CatalogueFixture.CreateAsync();

        await fixture.AddListingAsync(
            masterProductId: fixture.MasterProductId,
            waitingKey: null,
            supplierProductId: "unknown-pack",
            unitsPerPack: null);

        Assert.Null(Assert.Single(await fixture.ListingsAsync()).UnitsPerPack);
    }

    /// <summary>
    /// A barcode and its scope travel together in the schema as well as in the policy: a carton code
    /// read as a unit code prices one can at the price of thirty.
    /// </summary>
    [Fact]
    public async Task A_barcode_without_a_scope_is_refused_by_the_database()
    {
        await using var fixture = await CatalogueFixture.CreateAsync();

        await Assert.ThrowsAsync<DbUpdateException>(
            () => fixture.AddListingAsync(
                masterProductId: fixture.MasterProductId,
                waitingKey: null,
                supplierProductId: "scopeless",
                barcode: "9300675024235"));

        Assert.Empty(await fixture.ListingsAsync());
    }

    [Fact]
    public async Task A_barcode_scope_without_a_barcode_is_refused_by_the_database()
    {
        await using var fixture = await CatalogueFixture.CreateAsync();

        await Assert.ThrowsAsync<DbUpdateException>(
            () => fixture.AddListingAsync(
                masterProductId: fixture.MasterProductId,
                waitingKey: null,
                supplierProductId: "codeless",
                barcodeScope: BarcodeScope.Carton));

        Assert.Empty(await fixture.ListingsAsync());
    }

    /// <summary>
    /// A barcode keeps its leading zeros through the column as well as through the policy: the column
    /// is <c>TEXT</c>, so nothing on the round trip parses it.
    /// </summary>
    [Fact]
    public async Task A_stored_barcode_keeps_its_leading_zeros()
    {
        await using var fixture = await CatalogueFixture.CreateAsync();

        await fixture.AddListingAsync(
            masterProductId: fixture.MasterProductId,
            waitingKey: null,
            supplierProductId: "barcoded",
            barcode: "0012345678905",
            barcodeScope: BarcodeScope.Unit);

        var listing = Assert.Single(await fixture.ListingsAsync());
        Assert.Equal("0012345678905", listing.Barcode);
        Assert.Equal(BarcodeScope.Unit, listing.BarcodeScope);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-375)]
    public async Task A_master_product_size_that_is_not_positive_is_refused_by_the_database(decimal size)
    {
        await using var fixture = await CatalogueFixture.CreateAsync();

        await Assert.ThrowsAsync<DbUpdateException>(
            () => fixture.AddMasterProductAsync("Pepsi", "Max", size, MasterProductSizeUnit.Millilitre));
    }

    #endregion

    #region One proposal per suggestion, and nothing is ever deleted

    [Fact]
    public async Task A_suggestion_proposing_both_a_master_product_and_a_new_one_is_refused_by_the_database()
    {
        await using var fixture = await CatalogueFixture.CreateAsync();

        await Assert.ThrowsAsync<DbUpdateException>(
            () => fixture.AddSuggestionAsync(
                ProductMatchSuggestionStatus.Tentative,
                masterProductId: fixture.MasterProductId,
                suggestedBrand: "Coca-Cola",
                suggestedName: "Classic",
                suggestedSizeValue: 375m,
                suggestedSizeUnit: MasterProductSizeUnit.Millilitre));

        Assert.Empty(await fixture.SuggestionsAsync());
    }

    [Fact]
    public async Task A_suggestion_proposing_neither_is_refused_by_the_database()
    {
        await using var fixture = await CatalogueFixture.CreateAsync();

        await Assert.ThrowsAsync<DbUpdateException>(
            () => fixture.AddSuggestionAsync(
                ProductMatchSuggestionStatus.Tentative,
                masterProductId: null));

        Assert.Empty(await fixture.SuggestionsAsync());
    }

    [Fact]
    public async Task A_suggestion_describing_a_new_master_product_only_in_part_is_refused_by_the_database()
    {
        await using var fixture = await CatalogueFixture.CreateAsync();

        await Assert.ThrowsAsync<DbUpdateException>(
            () => fixture.AddSuggestionAsync(
                ProductMatchSuggestionStatus.Tentative,
                masterProductId: null,
                suggestedBrand: "Coca-Cola",
                suggestedName: "Classic"));

        Assert.Empty(await fixture.SuggestionsAsync());
    }

    /// <summary>
    /// The criterion the suggestion table exists for: a rejected or undone (waiting key, master
    /// product) pair is <strong>kept</strong>, so the matcher of #941 can see that it must never
    /// apply that pair again - and a later tentative suggestion for the same pair is a third row
    /// rather than something that replaces the refusals.
    /// </summary>
    [Fact]
    public async Task A_rejected_or_undone_pair_is_kept_beside_a_later_suggestion_for_the_same_pair()
    {
        await using var fixture = await CatalogueFixture.CreateAsync();

        await fixture.AddSuggestionAsync(
            ProductMatchSuggestionStatus.Rejected,
            masterProductId: fixture.MasterProductId);
        await fixture.AddSuggestionAsync(
            ProductMatchSuggestionStatus.Undone,
            masterProductId: fixture.MasterProductId);
        await fixture.AddSuggestionAsync(
            ProductMatchSuggestionStatus.Tentative,
            masterProductId: fixture.MasterProductId);

        var suggestions = await fixture.SuggestionsAsync();

        Assert.Equal(
            [
                ProductMatchSuggestionStatus.Rejected,
                ProductMatchSuggestionStatus.Undone,
                ProductMatchSuggestionStatus.Tentative,
            ],
            suggestions.Select(suggestion => suggestion.Status));
        Assert.All(suggestions, suggestion =>
        {
            Assert.Equal(fixture.WaitingKey, suggestion.WaitingKey);
            Assert.Equal(fixture.MasterProductId, suggestion.MasterProductId);
        });

        // And the two that block the matcher are recognised as such by the one rule that decides it.
        Assert.Equal(
            [true, true, false],
            suggestions
                .Select(suggestion => ProductMatchSuggestionPolicy.BlocksAutomaticMatching(suggestion.Status))
                .ToArray());
    }

    /// <summary>
    /// And the master product a kept refusal names cannot be deleted from under it: the reference
    /// restricts, so the evidence that a pair was refused cannot be removed by removing the product
    /// it was about.
    /// </summary>
    [Fact]
    public async Task A_master_product_a_suggestion_names_cannot_be_deleted()
    {
        await using var fixture = await CatalogueFixture.CreateAsync();
        await fixture.AddSuggestionAsync(
            ProductMatchSuggestionStatus.Rejected,
            masterProductId: fixture.MasterProductId);

        await Assert.ThrowsAsync<DbUpdateException>(fixture.DeleteMasterProductAsync);

        Assert.Single(await fixture.SuggestionsAsync());
    }

    #endregion

    #region Uniqueness and restricted references

    /// <summary>
    /// One description is one master product, and it is the database's rule rather than a query's -
    /// a <c>WHERE</c> clause checked before an insert is a check two concurrent inserts can both
    /// pass, and two master products for one product is what the parent issue exists to prevent.
    /// </summary>
    [Fact]
    public async Task Two_master_products_with_one_normalised_key_are_refused()
    {
        await using var fixture = await CatalogueFixture.CreateAsync();

        // The same description, spelled differently: the normalised key folds case and spacing, so
        // this is the same product arriving twice.
        await Assert.ThrowsAsync<DbUpdateException>(
            () => fixture.AddMasterProductAsync(
                "  coca-cola",
                "CLASSIC ",
                375m,
                MasterProductSizeUnit.Millilitre));
    }

    /// <summary>One supplier lists one product once, which is what the collector work list deduplicates by.</summary>
    [Fact]
    public async Task Two_listings_of_one_supplier_product_id_at_one_supplier_are_refused()
    {
        await using var fixture = await CatalogueFixture.CreateAsync();
        await fixture.AddListingAsync(
            masterProductId: fixture.MasterProductId,
            waitingKey: null,
            supplierProductId: "SKU-1");

        await Assert.ThrowsAsync<DbUpdateException>(
            () => fixture.AddListingAsync(
                masterProductId: fixture.MasterProductId,
                waitingKey: null,
                supplierProductId: "SKU-1"));

        Assert.Single(await fixture.ListingsAsync());
    }

    /// <summary>
    /// And the same supplier product id at a <em>different</em> supplier is a different listing: the
    /// constraint is per supplier master record, because two retailers may legitimately issue the
    /// same SKU - the same reason every constraint over an externally supplied value in this schema
    /// is scoped.
    /// </summary>
    [Fact]
    public async Task One_supplier_product_id_at_two_suppliers_is_two_listings()
    {
        await using var fixture = await CatalogueFixture.CreateAsync();
        var otherSupplier = await fixture.AddSupplierPresetAsync("Beta Grocer");

        await fixture.AddListingAsync(
            masterProductId: fixture.MasterProductId,
            waitingKey: null,
            supplierProductId: "SKU-1");
        await fixture.AddListingAsync(
            masterProductId: fixture.MasterProductId,
            waitingKey: null,
            supplierProductId: "SKU-1",
            supplierPresetId: otherSupplier);

        Assert.Equal(2, (await fixture.ListingsAsync()).Count);
    }

    [Fact]
    public async Task A_supplier_master_record_with_listings_cannot_be_deleted()
    {
        await using var fixture = await CatalogueFixture.CreateAsync();
        await fixture.AddListingAsync(
            masterProductId: fixture.MasterProductId,
            waitingKey: null,
            supplierProductId: "SKU-1");

        await Assert.ThrowsAsync<DbUpdateException>(fixture.DeleteSupplierPresetAsync);

        Assert.Single(await fixture.ListingsAsync());
    }

    [Fact]
    public async Task A_master_product_with_listings_cannot_be_deleted()
    {
        await using var fixture = await CatalogueFixture.CreateAsync();
        await fixture.AddListingAsync(
            masterProductId: fixture.MasterProductId,
            waitingKey: null,
            supplierProductId: "SKU-1");

        await Assert.ThrowsAsync<DbUpdateException>(fixture.DeleteMasterProductAsync);

        Assert.Single(await fixture.ListingsAsync());
    }

    #endregion

    /// <summary>
    /// A migrated SQLite database with one supplier master record and one master product in it, which
    /// is the smallest world in which the catalogue's own constraints can be stated.
    ///
    /// <para>No business is seeded and no scope is taken: every table these tests touch is a platform
    /// table, so an unrestricted context is doing setup rather than standing in for a request. The
    /// tenant-owned half of this catalogue is tested over two businesses in
    /// <c>EfProductPriceTrackingStoreTests</c>, where the boundary is what is under test.</para>
    /// </summary>
    private sealed class CatalogueFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AppDbContext> _options;

        private CatalogueFixture(SqliteConnection connection, DbContextOptions<AppDbContext> options)
        {
            _connection = connection;
            _options = options;
        }

        public int SupplierPresetId { get; private set; }

        public long MasterProductId { get; private set; }

        /// <summary>A waiting key of the shape the tracking table produces, for the rows that need one.</summary>
        public string WaitingKey { get; } =
            PriceCatalogueWaitingKey.For("Coca-Cola Classic", "375mL");

        public static async Task<CatalogueFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var fixture = new CatalogueFixture(
                connection,
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);

            await using var setup = TestAppDbContext.Unrestricted(fixture._options);
            await setup.Database.MigrateAsync();

            // A name none of the nine shipped presets holds: those are inserted by their own
            // migrations and NormalisedName is unique, so a fixture that reused one would be
            // testing the seed rather than this catalogue.
            fixture.SupplierPresetId = await fixture.AddSupplierPresetAsync("Alpha Grocer");
            fixture.MasterProductId = await fixture.AddMasterProductAsync(
                "Coca-Cola",
                "Classic",
                375m,
                MasterProductSizeUnit.Millilitre);

            return fixture;
        }

        public async Task<int> AddSupplierPresetAsync(string name)
        {
            await using var db = TestAppDbContext.Unrestricted(_options);

            var preset = new SupplierPreset
            {
                Name = name,
                NormalisedName = SupplierPresetPolicy.NormalisedName(name),
                IsEnabled = true,
                ServesNationwide = true,
                CreatedAtUtc = RecordedAt,
                UpdatedAtUtc = RecordedAt,
            };

            db.SupplierPresets.Add(preset);
            await db.SaveChangesAsync();

            return preset.Id;
        }

        public async Task<long> AddMasterProductAsync(
            string brand,
            string name,
            decimal size,
            MasterProductSizeUnit unit)
        {
            await using var db = TestAppDbContext.Unrestricted(_options);

            var master = new MasterProduct
            {
                Brand = brand.Trim(),
                Name = name.Trim(),
                SizeValue = size,
                SizeUnit = unit,
                // Built by the one rule that builds it, so the stored key under test is the key the
                // application would really have written - including for the "same description,
                // different spelling" case.
                NormalisedKey = BuildKey(brand, name, size, unit),
                CreatedAtUtc = RecordedAt,
                UpdatedAtUtc = RecordedAt,
            };

            db.MasterProducts.Add(master);
            await db.SaveChangesAsync();

            return master.Id;
        }

        public async Task AddListingAsync(
            long? masterProductId,
            string? waitingKey,
            string supplierProductId,
            string? originWaitingKey = null,
            int? unitsPerPack = null,
            string? barcode = null,
            BarcodeScope? barcodeScope = null,
            int? supplierPresetId = null)
        {
            await using var db = TestAppDbContext.Unrestricted(_options);

            db.SupplierListings.Add(new SupplierListing
            {
                SupplierPresetId = supplierPresetId ?? SupplierPresetId,
                MasterProductId = masterProductId,
                WaitingKey = waitingKey,
                OriginWaitingKey = originWaitingKey,
                SupplierProductId = supplierProductId,
                UnitsPerPack = unitsPerPack,
                Barcode = barcode,
                BarcodeScope = barcodeScope,
                MatchStatus = SupplierListingMatchStatus.Proposed,
                CreatedAtUtc = RecordedAt,
                UpdatedAtUtc = RecordedAt,
            });

            await db.SaveChangesAsync();
        }

        public async Task AddSuggestionAsync(
            ProductMatchSuggestionStatus status,
            long? masterProductId,
            string? suggestedBrand = null,
            string? suggestedName = null,
            decimal? suggestedSizeValue = null,
            MasterProductSizeUnit? suggestedSizeUnit = null)
        {
            await using var db = TestAppDbContext.Unrestricted(_options);

            db.ProductMatchSuggestions.Add(new ProductMatchSuggestion
            {
                WaitingKey = WaitingKey,
                MasterProductId = masterProductId,
                SuggestedBrand = suggestedBrand,
                SuggestedName = suggestedName,
                SuggestedSizeValue = suggestedSizeValue,
                SuggestedSizeUnit = suggestedSizeUnit,
                Confidence = 0.9m,
                Reason = "Exact brand, name and size",
                CollectorRun = "run-2026-10-27-01",
                Status = status,
                CreatedAtUtc = RecordedAt,
                UpdatedAtUtc = RecordedAt,
            });

            await db.SaveChangesAsync();
        }

        public async Task DeleteMasterProductAsync()
        {
            await using var db = TestAppDbContext.Unrestricted(_options);

            db.MasterProducts.Remove(
                await db.MasterProducts.SingleAsync(master => master.Id == MasterProductId));

            await db.SaveChangesAsync();
        }

        public async Task DeleteSupplierPresetAsync()
        {
            await using var db = TestAppDbContext.Unrestricted(_options);

            db.SupplierPresets.Remove(
                await db.SupplierPresets.SingleAsync(preset => preset.Id == SupplierPresetId));

            await db.SaveChangesAsync();
        }

        public async Task<IReadOnlyList<SupplierListing>> ListingsAsync()
        {
            await using var db = TestAppDbContext.Unrestricted(_options);

            return await db.SupplierListings.AsNoTracking().OrderBy(listing => listing.Id).ToListAsync();
        }

        public async Task<IReadOnlyList<ProductMatchSuggestion>> SuggestionsAsync()
        {
            await using var db = TestAppDbContext.Unrestricted(_options);

            return await db.ProductMatchSuggestions
                .AsNoTracking()
                .OrderBy(suggestion => suggestion.Id)
                .ToListAsync();
        }

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

        /// <summary>
        /// The normalised key the application would store for this description, or - for a
        /// description the policy refuses, which the size tests submit deliberately - a key built from
        /// the raw parts, so the row still reaches the database and is refused there rather than in
        /// this helper.
        /// </summary>
        private static string BuildKey(
            string brand,
            string name,
            decimal size,
            MasterProductSizeUnit unit)
        {
            try
            {
                return MasterProductPolicy.NormalisedKey(brand, name, size, unit);
            }
            catch (Inventory.Domain.Exceptions.DomainValidationException)
            {
                return PriceCatalogueKey.Of(brand, name, size.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), unit.ToString());
            }
        }
    }
}
