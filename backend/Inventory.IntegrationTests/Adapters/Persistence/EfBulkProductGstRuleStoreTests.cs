using Inventory.Application.Products;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Gst;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational tests for the bulk product GST rule adapter (issue #1076): the count the operator
/// confirms, and the one transaction that configures exactly the products that count was about.
///
/// <para>Two businesses over real SQLite and the real migration history, as AGENTS.md § Tenant
/// ownership requires of any change that writes many tenant-owned rows at once: what is under test
/// is the boundary itself - the central query filters, the ownership enforcement on save, and a
/// transaction whose count, comparison and write are one operation. An InMemory provider has none
/// of those, and one business cannot express a cross-business write at all.</para>
///
/// <para>Every test also compares the products' catalogue and costing state across the write, because
/// the whole claim of this feature is that it is <em>configuration</em>: the rule column moves and
/// the name, unit, stock, cost, valuation and <c>UpdatedAt</c> do not (AGENTS.md § Purchase GST
/// classification).</para>
/// </summary>
public class EfBulkProductGstRuleStoreTests
{
    #region The count

    [Fact]
    public async Task The_count_is_the_products_carrying_no_rule_active_and_inactive_alike()
    {
        await using var fixture = await BulkGstRuleFixture.CreateAsync();
        await fixture.SetStoredRuleAsync(fixture.FirstBusinessTaxableId, GstClassification.Taxable);

        var count = await fixture.StoreFor(fixture.FirstBusinessId)
            .CountProductsWithNoRuleAsync(CancellationToken.None);

        // Two with no rule: one active, one inactive. The third carries an explicit Taxable.
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task The_count_sees_only_the_callers_own_business()
    {
        await using var fixture = await BulkGstRuleFixture.CreateAsync();

        var second = await fixture.StoreFor(fixture.SecondBusinessId)
            .CountProductsWithNoRuleAsync(CancellationToken.None);

        // The second business has exactly one product of its own, and the first business's three
        // are invisible to it.
        Assert.Equal(1, second);
    }

    [Fact]
    public async Task Nothing_eligible_is_counted_as_zero()
    {
        await using var fixture = await BulkGstRuleFixture.CreateAsync();
        await fixture.GiveEveryFirstBusinessProductARuleAsync(GstClassification.GstFree);

        var count = await fixture.StoreFor(fixture.FirstBusinessId)
            .CountProductsWithNoRuleAsync(CancellationToken.None);

        Assert.Equal(0, count);
    }

    /// <summary>
    /// Fail closed: a caller with no resolvable business counts nothing, because the central filters
    /// match none of either business's rows.
    /// </summary>
    [Fact]
    public async Task An_unresolved_business_counts_nothing()
    {
        await using var fixture = await BulkGstRuleFixture.CreateAsync();

        var count = await fixture.DeniedStore()
            .CountProductsWithNoRuleAsync(CancellationToken.None);

        Assert.Equal(0, count);
    }

    #endregion

    #region The write

    [Theory]
    [InlineData(GstClassification.Taxable)]
    [InlineData(GstClassification.GstFree)]
    public async Task Every_eligible_product_is_configured_including_an_inactive_one(
        GstClassification target)
    {
        await using var fixture = await BulkGstRuleFixture.CreateAsync();
        await fixture.SetStoredRuleAsync(fixture.FirstBusinessTaxableId, GstClassification.Taxable);
        var before = await fixture.CatalogueStateAsync();

        var changed = await fixture.StoreFor(fixture.FirstBusinessId)
            .SetRuleOnProductsWithNoRuleAsync(target, expectedEligibleCount: 2, CancellationToken.None);

        Assert.Equal(2, changed);
        Assert.Equal(target, await fixture.StoredRuleAsync(fixture.FirstBusinessActiveId));
        Assert.Equal(target, await fixture.StoredRuleAsync(fixture.FirstBusinessInactiveId));
        Assert.Equal(before, await fixture.CatalogueStateAsync());
    }

    /// <summary>
    /// The rule this action exists to preserve: an explicitly configured rule is somebody's
    /// decision and is never replaced - not even by a change to the rule it already carries.
    /// </summary>
    [Fact]
    public async Task An_explicitly_configured_rule_is_left_exactly_as_it_is()
    {
        await using var fixture = await BulkGstRuleFixture.CreateAsync();
        await fixture.SetStoredRuleAsync(fixture.FirstBusinessTaxableId, GstClassification.GstFree);

        await fixture.StoreFor(fixture.FirstBusinessId)
            .SetRuleOnProductsWithNoRuleAsync(
                GstClassification.GstFree, expectedEligibleCount: 2, CancellationToken.None);

        Assert.Equal(
            GstClassification.GstFree, await fixture.StoredRuleAsync(fixture.FirstBusinessTaxableId));
    }

    [Fact]
    public async Task No_other_businesss_product_is_written()
    {
        await using var fixture = await BulkGstRuleFixture.CreateAsync();

        var changed = await fixture.StoreFor(fixture.FirstBusinessId)
            .SetRuleOnProductsWithNoRuleAsync(
                GstClassification.Taxable, expectedEligibleCount: 3, CancellationToken.None);

        Assert.Equal(3, changed);
        Assert.Equal(GstRules.None, await fixture.StoredRuleAsync(fixture.SecondBusinessProductId));
    }

    [Fact]
    public async Task A_confirmed_count_of_zero_over_nothing_eligible_writes_nothing()
    {
        await using var fixture = await BulkGstRuleFixture.CreateAsync();
        await fixture.GiveEveryFirstBusinessProductARuleAsync(GstClassification.Taxable);
        var before = await fixture.CatalogueStateAsync();

        var changed = await fixture.StoreFor(fixture.FirstBusinessId)
            .SetRuleOnProductsWithNoRuleAsync(
                GstClassification.GstFree, expectedEligibleCount: 0, CancellationToken.None);

        Assert.Equal(0, changed);
        Assert.Equal(before, await fixture.CatalogueStateAsync());
        Assert.Equal(
            GstClassification.Taxable, await fixture.StoredRuleAsync(fixture.FirstBusinessActiveId));
    }

    /// <summary>
    /// Fail closed on the write half: a caller with no resolvable business has nothing eligible, so
    /// a confirmed count of zero is the only request it can even make - and it writes nothing in
    /// either business.
    /// </summary>
    [Fact]
    public async Task An_unresolved_business_writes_nothing()
    {
        await using var fixture = await BulkGstRuleFixture.CreateAsync();
        var before = await fixture.CatalogueStateAsync();

        var changed = await fixture.DeniedStore()
            .SetRuleOnProductsWithNoRuleAsync(
                GstClassification.Taxable, expectedEligibleCount: 0, CancellationToken.None);

        Assert.Equal(0, changed);
        Assert.Equal(before, await fixture.CatalogueStateAsync());
    }

    #endregion

    #region The confirmed count, and what moved since

    /// <summary>
    /// The stale-confirmation regression: the operator confirmed a count, a product was configured
    /// between that read and this write, and the recount inside the transaction refuses the whole
    /// change rather than applying it to a set nobody approved.
    /// </summary>
    [Fact]
    public async Task A_rule_configured_between_the_preview_and_the_write_refuses_the_whole_change()
    {
        await using var fixture = await BulkGstRuleFixture.CreateAsync();
        var confirmed = await fixture.StoreFor(fixture.FirstBusinessId)
            .CountProductsWithNoRuleAsync(CancellationToken.None);
        Assert.Equal(3, confirmed);

        // Another caller, committing in between.
        await fixture.SetStoredRuleAsync(fixture.FirstBusinessTaxableId, GstClassification.Taxable);
        var before = await fixture.CatalogueStateAsync();

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(
            () => fixture.StoreFor(fixture.FirstBusinessId).SetRuleOnProductsWithNoRuleAsync(
                GstClassification.GstFree, confirmed, CancellationToken.None));

        Assert.Equal(BulkProductGstRulePolicy.EligibleCountChangedMessage, refusal.Message);
        Assert.Equal(before, await fixture.CatalogueStateAsync());
        Assert.Equal(GstRules.None, await fixture.StoredRuleAsync(fixture.FirstBusinessActiveId));
        Assert.Equal(GstRules.None, await fixture.StoredRuleAsync(fixture.FirstBusinessInactiveId));
    }

    [Fact]
    public async Task A_product_created_between_the_preview_and_the_write_refuses_it_too()
    {
        await using var fixture = await BulkGstRuleFixture.CreateAsync();
        var confirmed = await fixture.StoreFor(fixture.FirstBusinessId)
            .CountProductsWithNoRuleAsync(CancellationToken.None);

        await fixture.SeedProductAsync(fixture.FirstBusinessId, "Late Arrival", isActive: true);

        await Assert.ThrowsAsync<DomainConflictException>(
            () => fixture.StoreFor(fixture.FirstBusinessId).SetRuleOnProductsWithNoRuleAsync(
                GstClassification.Taxable, confirmed, CancellationToken.None));

        Assert.Equal(GstRules.None, await fixture.StoredRuleAsync(fixture.FirstBusinessActiveId));
    }

    /// <summary>
    /// The eligibility predicate protects an explicit rule independently of the count, which is
    /// what makes "a concurrent explicit rule is never overwritten" true rather than merely likely:
    /// here one product gains an explicit rule and another with no rule is created, so the count
    /// still agrees - and the explicit one is still not written.
    /// </summary>
    [Fact]
    public async Task An_explicit_rule_committed_in_between_is_not_written_even_when_the_count_agrees()
    {
        await using var fixture = await BulkGstRuleFixture.CreateAsync();
        var confirmed = await fixture.StoreFor(fixture.FirstBusinessId)
            .CountProductsWithNoRuleAsync(CancellationToken.None);

        await fixture.SetStoredRuleAsync(fixture.FirstBusinessTaxableId, GstClassification.Taxable);
        var created = await fixture.SeedProductAsync(
            fixture.FirstBusinessId, "Late Arrival", isActive: true);

        var changed = await fixture.StoreFor(fixture.FirstBusinessId)
            .SetRuleOnProductsWithNoRuleAsync(GstClassification.GstFree, confirmed, CancellationToken.None);

        Assert.Equal(confirmed, changed);
        Assert.Equal(
            GstClassification.Taxable, await fixture.StoredRuleAsync(fixture.FirstBusinessTaxableId));
        Assert.Equal(GstClassification.GstFree, await fixture.StoredRuleAsync(created));
    }

    #endregion

    #region One transaction

    /// <summary>
    /// All or none. The write is staged inside the adapter's own transaction and the commit is the
    /// last thing it does, so a failure after the rows were written to the database still leaves the
    /// catalogue exactly as it was - which is what the acceptance criterion "either every eligible
    /// update succeeds or none persist" asks for.
    ///
    /// <para>The failure is injected at the one point that proves it: <c>SavedChanges</c> is raised
    /// after the rows have been sent to SQLite and before this adapter commits. Without the explicit
    /// transaction, <c>SaveChanges</c>'s own implicit one would already have committed them.</para>
    /// </summary>
    [Fact]
    public async Task A_failure_after_the_rows_are_written_rolls_every_one_of_them_back()
    {
        await using var fixture = await BulkGstRuleFixture.CreateAsync();
        var before = await fixture.CatalogueStateAsync();
        var db = fixture.ContextFor(fixture.FirstBusinessId);
        db.SavedChanges += (_, _) => throw new InvalidOperationException("Injected failure.");

        await Assert.ThrowsAnyAsync<Exception>(
            () => new EfBulkProductGstRuleStore(db).SetRuleOnProductsWithNoRuleAsync(
                GstClassification.Taxable, expectedEligibleCount: 3, CancellationToken.None));

        Assert.Equal(before, await fixture.CatalogueStateAsync());
        Assert.Equal(GstRules.None, await fixture.StoredRuleAsync(fixture.FirstBusinessActiveId));
        Assert.Equal(GstRules.None, await fixture.StoredRuleAsync(fixture.FirstBusinessInactiveId));
        Assert.Equal(GstRules.None, await fixture.StoredRuleAsync(fixture.FirstBusinessTaxableId));
    }

    #endregion

    /// <summary>
    /// A migrated SQLite database with two businesses: the first with three products - an active
    /// one, an inactive one and one a test gives an explicit rule - and the second with one of its
    /// own, which nothing the first business does may reach.
    /// </summary>
    private sealed class BulkGstRuleFixture : IAsyncDisposable
    {
        private static readonly DateTime CreatedAt = new(2026, 11, 3, 1, 0, 0, DateTimeKind.Utc);

        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AppDbContext> _options;
        private readonly List<AppDbContext> _contexts = [];

        private BulkGstRuleFixture(SqliteConnection connection, DbContextOptions<AppDbContext> options)
        {
            _connection = connection;
            _options = options;
        }

        public int FirstBusinessId { get; private set; }

        public int SecondBusinessId { get; private set; }

        /// <summary>The first business's active product, which starts with no rule.</summary>
        public long FirstBusinessActiveId { get; private set; }

        /// <summary>
        /// The first business's <em>inactive</em> product, which also starts with no rule: the scope
        /// the operator confirms explicitly includes it.
        /// </summary>
        public long FirstBusinessInactiveId { get; private set; }

        /// <summary>The first business's third product, for the tests that configure one explicitly.</summary>
        public long FirstBusinessTaxableId { get; private set; }

        public long SecondBusinessProductId { get; private set; }

        public static async Task<BulkGstRuleFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var fixture = new BulkGstRuleFixture(
                connection,
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);

            await using (var setup = TestAppDbContext.Unrestricted(fixture._options))
            {
                await setup.Database.MigrateAsync();
            }

            fixture.FirstBusinessId = await fixture.SeedBusinessAsync("Coastal Vending");
            fixture.SecondBusinessId = await fixture.SeedBusinessAsync("Vending Co");

            fixture.FirstBusinessActiveId = await fixture.SeedProductAsync(
                fixture.FirstBusinessId, "Coca-Cola Classic", isActive: true);
            fixture.FirstBusinessInactiveId = await fixture.SeedProductAsync(
                fixture.FirstBusinessId, "Retired Chips", isActive: false);
            fixture.FirstBusinessTaxableId = await fixture.SeedProductAsync(
                fixture.FirstBusinessId, "Pepsi Max", isActive: true);
            fixture.SecondBusinessProductId = await fixture.SeedProductAsync(
                fixture.SecondBusinessId, "Other Business Chocolate", isActive: true);

            return fixture;
        }

        /// <summary>
        /// The adapter as a request reaches it: the real store on a context scoped to one business,
        /// which is the only way the central filters and the ownership enforcement are under test.
        /// </summary>
        public IBulkProductGstRuleStore StoreFor(int businessId) =>
            new EfBulkProductGstRuleStore(ContextFor(businessId));

        /// <summary>The adapter on a context whose caller has no usable membership.</summary>
        public IBulkProductGstRuleStore DeniedStore() =>
            new EfBulkProductGstRuleStore(Track(TestAppDbContext.Denied(_options)));

        /// <summary>
        /// A business-scoped context of this fixture's own, for the one test that has to reach
        /// inside the adapter's save. A new one each time, because every call a request makes is its
        /// own request and a reused change tracker would assert against entities a previous call
        /// left behind.
        /// </summary>
        public AppDbContext ContextFor(int businessId) =>
            Track(TestAppDbContext.For(_options, businessId));

        public async Task<long> SeedProductAsync(int businessId, string name, bool isActive)
        {
            await using var db = TestAppDbContext.Unrestricted(_options);

            var product = new Product
            {
                BusinessId = businessId,
                Name = name,
                Unit = "unit",
                IsActive = isActive,
                QuantityInStock = 12,
                AverageUnitCost = 1.25m,
                CreatedAt = CreatedAt,
                UpdatedAt = CreatedAt,
            };

            db.Products.Add(product);
            await db.SaveChangesAsync();

            return product.Id;
        }

        /// <summary>Configures one product's rule from outside the adapter, as another caller would.</summary>
        public async Task SetStoredRuleAsync(long productId, GstClassification rule)
        {
            await using var db = TestAppDbContext.Unrestricted(_options);

            var product = await db.Products.SingleAsync(candidate => candidate.Id == productId);
            product.GstRule = rule;
            await db.SaveChangesAsync();
        }

        public async Task GiveEveryFirstBusinessProductARuleAsync(GstClassification rule)
        {
            await SetStoredRuleAsync(FirstBusinessActiveId, rule);
            await SetStoredRuleAsync(FirstBusinessInactiveId, rule);
            await SetStoredRuleAsync(FirstBusinessTaxableId, rule);
        }

        public async Task<GstClassification> StoredRuleAsync(long productId)
        {
            await using var db = TestAppDbContext.Unrestricted(_options);

            return (await db.Products.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == productId)).GstRule;
        }

        /// <summary>
        /// Every product of both businesses with everything about it <em>except</em> its GST rule,
        /// as one comparable value - so a bulk rule change can be shown to have moved the rule
        /// column and nothing else at all: not a name, a unit, a stock quantity, a cost, a
        /// valuation, an activation flag or the catalogue record's own <c>UpdatedAt</c>.
        /// </summary>
        public async Task<string> CatalogueStateAsync()
        {
            await using var db = TestAppDbContext.Unrestricted(_options);

            var products = await db.Products.AsNoTracking().OrderBy(product => product.Id)
                .Select(product =>
                    $"{product.Id}:{product.BusinessId}:{product.Name}:{product.Sku}:{product.Unit}:" +
                    $"{product.IsActive}:{product.QuantityInStock}:{product.AverageUnitCost}:" +
                    $"{product.CostingQuantity}:{product.InventoryValue}:{product.CategoryId}:" +
                    $"{product.CreatedAt:O}:{product.UpdatedAt:O}")
                .ToListAsync();

            return string.Join("|", products);
        }

        private async Task<int> SeedBusinessAsync(string name)
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

        private AppDbContext Track(AppDbContext context)
        {
            _contexts.Add(context);
            return context;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var context in _contexts)
            {
                await context.DisposeAsync();
            }

            await _connection.DisposeAsync();
        }
    }
}
