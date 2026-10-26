using Inventory.Application.Costing;
using Inventory.Application.Products;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Products;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational coverage for the manual catalogue's product write surface (issue #736, parent #329
/// decision P2): the create/edit/delete round trip, the fields a sales provider keeps, the category
/// a product may point at, and every kind of history that refuses a delete.
///
/// <para>SQLite rather than an in-memory provider, because what is asserted is relational: two of
/// the references the delete is refused over <strong>cascade</strong> (stock movements, machine stock
/// counts) and four are <strong>restricted</strong> (purchase lines, supplier order lines, sales,
/// costing records), so an in-memory test could not tell a refusal apart from a delete that silently
/// erased history or failed inside the database.</para>
/// </summary>
public class EfProductCatalogueWriteTests : IDisposable
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public EfProductCatalogueWriteTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
        setup.Businesses.AddRange(
            NayaxBusiness(BusinessA, "Vending A"),
            NayaxBusiness(BusinessB, "Vending B"));
        setup.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Both businesses sell through Nayax, which is what makes an external id on one of their rows a
    /// Nayax id and lets a refusal name the provider (issue #759).
    /// </summary>
    private static Business NayaxBusiness(int id, string name) => new()
    {
        Id = id,
        Name = name,
        Status = BusinessStatus.Active,
        SalesProvider = ExternalProvider.Nayax,
        CreatedAtUtc = DateTime.UtcNow,
    };

    private static ProductCreateFields CreateFields(
        string name = "Coke",
        int quantityInStock = 0,
        long? categoryId = null,
        decimal? initialUnitCost = null,
        string? unit = "can") =>
        new(name, "SKU-1", "A can", quantityInStock, 5, 10, unit, categoryId, true, initialUnitCost);

    private static ProductUpdateFields UpdateFields(
        string name = "Coke",
        string? unit = "can",
        long? categoryId = null,
        bool isActive = true) =>
        new(name, "SKU-1", "A can", 5, 10, unit, categoryId, isActive);

    private EfProductStore StoreFor(AppDbContext db) => new(db, TestCostingUseCases.Rebuild(db));

    /// <summary>
    /// The round trip the acceptance criteria ask for: a business with no catalogue creates a
    /// product, edits the three fields that used to be the import's alone, and deletes it again
    /// while nothing refers to it.
    /// </summary>
    [Fact]
    public async Task A_product_is_created_edited_and_deleted()
    {
        long categoryId;
        await using (var seed = TestAppDbContext.For(_options, BusinessA))
        {
            var category = new Category { Name = "Drinks" };
            seed.Categories.Add(category);
            await seed.SaveChangesAsync();
            categoryId = category.Id;
        }

        long productId;
        await using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            var created = await StoreFor(db).CreateAsync(CreateFields(), CancellationToken.None);

            Assert.Equal(ProductWriteOutcome.Succeeded, created.Outcome);
            productId = created.ProductId!.Value;
        }

        await using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            var updated = await StoreFor(db).UpdateAsync(
                productId,
                UpdateFields(name: "Coke Zero", unit: "bottle", categoryId: categoryId),
                CancellationToken.None);

            Assert.Equal(ProductWriteOutcome.Succeeded, updated.Outcome);
        }

        await using (var verify = TestAppDbContext.For(_options, BusinessA))
        {
            var product = await verify.Products.AsNoTracking().SingleAsync(p => p.Id == productId);
            Assert.Equal(("Coke Zero", "bottle", (long?)categoryId), (product.Name, product.Unit, product.CategoryId));
        }

        await using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            var deleted = await StoreFor(db).DeleteAsync(productId, CancellationToken.None);

            Assert.Equal(ProductWriteOutcome.Succeeded, deleted.Outcome);
        }

        await using var after = TestAppDbContext.For(_options, BusinessA);
        Assert.False(await after.Products.AnyAsync(p => p.Id == productId));
    }

    /// <summary>
    /// A product created with opening stock keeps the initial restock movement the create has always
    /// recorded - the create path is unchanged by this issue, and that movement is immediately
    /// history the delete is refused over.
    /// </summary>
    [Fact]
    public async Task A_created_product_with_opening_stock_records_its_initial_movement()
    {
        await using var db = TestAppDbContext.For(_options, BusinessA);

        var created = await StoreFor(db).CreateAsync(
            CreateFields(quantityInStock: 6), CancellationToken.None);

        Assert.Equal(ProductWriteOutcome.Succeeded, created.Outcome);
        var movement = await db.StockAdjustments.AsNoTracking()
            .SingleAsync(adjustment => adjustment.ProductId == created.ProductId);
        Assert.Equal(6, movement.QuantityChange);
    }

    /// <summary>
    /// Creating a product with opening stock is <strong>one</strong> transaction (issue #736): the
    /// product, the movement that explains its quantity and the costing position that movement
    /// implies either all exist or none of them do.
    ///
    /// <para>The failure is injected into the cost rebuild, which is the last of the three writes
    /// and the one that runs <em>after</em> the product and its movement have already been saved -
    /// precisely the window in which the previous three-commit shape left a persisted quantity with
    /// no complete ledger behind it, under a request the caller saw fail and would retry. A
    /// cancellation is the same window reached the way a caller actually reaches it. Both are
    /// verified from a <em>reopened</em> context, because the one that wrote them still tracks what
    /// the rollback undid.</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failure_in_the_opening_cost_rebuild_leaves_no_product_movement_or_cost(bool cancelled)
    {
        Exception failure = cancelled
            ? new OperationCanceledException()
            : new InvalidOperationException("costing unavailable");

        await using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            var store = new EfProductStore(db, new FailingRebuild(failure));

            await Assert.ThrowsAsync(
                failure.GetType(),
                () => store.CreateAsync(
                    CreateFields(quantityInStock: 12, initialUnitCost: 2m), CancellationToken.None));
        }

        await using var after = TestAppDbContext.For(_options, BusinessA);
        Assert.Empty(await after.Products.AsNoTracking().ToListAsync());
        Assert.Empty(await after.StockAdjustments.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// A provider-linked product keeps its name and its category, and keeps every local field
    /// editable: the price edit in the same request shape is accepted and persisted.
    /// </summary>
    [Fact]
    public async Task A_provider_linked_products_name_may_not_change_but_its_price_may()
    {
        var productId = await SeedProductAsync(externalProductId: "100", name: "Nayax Coke");

        await using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            var refused = await StoreFor(db).UpdateAsync(
                productId, UpdateFields(name: "My Coke"), CancellationToken.None);

            Assert.Equal(ProductWriteOutcome.Refused, refused.Outcome);
            Assert.Equal(
                ProviderManagedCatalogueFields.ProductNameRefusal(ExternalProvider.Nayax),
                refused.Refusal);
        }

        await using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            var accepted = await StoreFor(db).UpdateAsync(
                productId, UpdateFields(name: "Nayax Coke", unit: "bottle"), CancellationToken.None);

            Assert.Equal(ProductWriteOutcome.Succeeded, accepted.Outcome);
        }

        await using var verify = TestAppDbContext.For(_options, BusinessA);
        var product = await verify.Products.AsNoTracking().SingleAsync(p => p.Id == productId);
        Assert.Equal(("Nayax Coke", "bottle"), (product.Name, product.Unit));
    }

    [Fact]
    public async Task A_provider_linked_products_category_may_not_change()
    {
        var productId = await SeedProductAsync(externalProductId: "100", name: "Nayax Coke");
        long categoryId;
        await using (var seed = TestAppDbContext.For(_options, BusinessA))
        {
            var category = new Category { Name = "Drinks" };
            seed.Categories.Add(category);
            await seed.SaveChangesAsync();
            categoryId = category.Id;
        }

        await using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            var refused = await StoreFor(db).UpdateAsync(
                productId,
                UpdateFields(name: "Nayax Coke", categoryId: categoryId),
                CancellationToken.None);

            Assert.Equal(ProductWriteOutcome.Refused, refused.Outcome);
            Assert.Equal(
                ProviderManagedCatalogueFields.ProductCategoryRefusal(ExternalProvider.Nayax),
                refused.Refusal);
        }

        await using var verify = TestAppDbContext.For(_options, BusinessA);
        Assert.Null((await verify.Products.AsNoTracking().SingleAsync(p => p.Id == productId)).CategoryId);
    }

    /// <summary>
    /// A local product - the normal case for a business with no provider - may be renamed and
    /// recategorised freely.
    /// </summary>
    [Fact]
    public async Task A_local_products_name_and_category_are_editable()
    {
        var productId = await SeedProductAsync(externalProductId: null, name: "Coke");
        long categoryId;
        await using (var seed = TestAppDbContext.For(_options, BusinessA))
        {
            var category = new Category { Name = "Drinks" };
            seed.Categories.Add(category);
            await seed.SaveChangesAsync();
            categoryId = category.Id;
        }

        await using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            var updated = await StoreFor(db).UpdateAsync(
                productId, UpdateFields(name: "Pepsi", categoryId: categoryId), CancellationToken.None);

            Assert.Equal(ProductWriteOutcome.Succeeded, updated.Outcome);
        }

        await using var verify = TestAppDbContext.For(_options, BusinessA);
        var product = await verify.Products.AsNoTracking().SingleAsync(p => p.Id == productId);
        Assert.Equal(("Pepsi", (long?)categoryId), (product.Name, product.CategoryId));
    }

    /// <summary>
    /// A hand-maintained product has a unit label, and what is stored is the trimmed value (issue
    /// #895): trailing space is not a different unit.
    /// </summary>
    [Fact]
    public async Task A_created_unit_label_is_stored_trimmed()
    {
        long productId;
        await using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            var created = await StoreFor(db).CreateAsync(
                CreateFields(unit: "  bottle  "), CancellationToken.None);

            Assert.Equal(ProductWriteOutcome.Succeeded, created.Outcome);
            productId = created.ProductId!.Value;
        }

        await using var verify = TestAppDbContext.For(_options, BusinessA);
        Assert.Equal("bottle", (await verify.Products.AsNoTracking().SingleAsync(p => p.Id == productId)).Unit);
    }

    /// <summary>
    /// A create with no unit label is refused and writes nothing at all - not the product, and not
    /// the opening stock movement a created product with a quantity would otherwise record (issue
    /// #895).
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_create_with_no_unit_label_is_refused_and_persists_nothing(string? unit)
    {
        await using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            var created = await StoreFor(db).CreateAsync(
                CreateFields(quantityInStock: 4, initialUnitCost: 1.5m, unit: unit), CancellationToken.None);

            Assert.Equal(ProductWriteOutcome.Refused, created.Outcome);
            Assert.Equal(ProductCataloguePolicy.UnitRequiredMessage, created.Refusal);
            Assert.Null(created.ProductId);
        }

        await using var verify = TestAppDbContext.For(_options, BusinessA);
        Assert.False(await verify.Products.AnyAsync());
        Assert.False(await verify.StockAdjustments.AnyAsync());
    }

    /// <summary>
    /// No request may blank a label a product already has (issue #895), and the refusal leaves the
    /// whole row as it was - including the active flag the same request carried.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_update_may_not_blank_a_stored_unit_label(string? unit)
    {
        var productId = await SeedProductAsync(externalProductId: null, name: "Coke", unit: "bag");

        await using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            var updated = await StoreFor(db).UpdateAsync(
                productId, UpdateFields(unit: unit, isActive: false), CancellationToken.None);

            Assert.Equal(ProductWriteOutcome.Refused, updated.Outcome);
            Assert.Equal(ProductCataloguePolicy.UnitRequiredMessage, updated.Refusal);
        }

        await using var verify = TestAppDbContext.For(_options, BusinessA);
        var product = await verify.Products.AsNoTracking().SingleAsync(p => p.Id == productId);
        Assert.Equal(("bag", true), (product.Unit, product.IsActive));
    }

    /// <summary>
    /// The asymmetry issue #895 exists for: a legacy product whose stored label is missing is still
    /// updatable with no label, because the Products list's Deactivate and Reactivate send the whole
    /// product back through this same write and a blanket rule would strand such a row forever. The
    /// label stays missing - nothing substitutes one and nothing is backfilled.
    /// </summary>
    [Fact]
    public async Task A_legacy_product_with_no_unit_label_can_still_be_deactivated_and_reactivated()
    {
        var productId = await SeedProductAsync(externalProductId: null, name: "Coke", unit: null);

        await using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            Assert.Equal(
                ProductWriteOutcome.Succeeded,
                (await StoreFor(db).UpdateAsync(
                    productId, UpdateFields(unit: null, isActive: false), CancellationToken.None)).Outcome);
        }

        await using (var verify = TestAppDbContext.For(_options, BusinessA))
        {
            var deactivated = await verify.Products.AsNoTracking().SingleAsync(p => p.Id == productId);
            Assert.False(deactivated.IsActive);
            Assert.True(string.IsNullOrWhiteSpace(deactivated.Unit));
        }

        await using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            Assert.Equal(
                ProductWriteOutcome.Succeeded,
                (await StoreFor(db).UpdateAsync(
                    productId, UpdateFields(unit: null, isActive: true), CancellationToken.None)).Outcome);
        }

        await using var after = TestAppDbContext.For(_options, BusinessA);
        var reactivated = await after.Products.AsNoTracking().SingleAsync(p => p.Id == productId);
        Assert.True(reactivated.IsActive);
        Assert.True(string.IsNullOrWhiteSpace(reactivated.Unit));
    }

    /// <summary>
    /// A legacy row is given its label by the ordinary edit, trimmed like any other (issue #895),
    /// and an existing label is replaced the same way.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("bag")]
    public async Task An_updated_unit_label_is_stored_trimmed(string? storedUnit)
    {
        var productId = await SeedProductAsync(externalProductId: null, name: "Coke", unit: storedUnit);

        await using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            Assert.Equal(
                ProductWriteOutcome.Succeeded,
                (await StoreFor(db).UpdateAsync(
                    productId, UpdateFields(unit: "  bottle  "), CancellationToken.None)).Outcome);
        }

        await using var verify = TestAppDbContext.For(_options, BusinessA);
        Assert.Equal("bottle", (await verify.Products.AsNoTracking().SingleAsync(p => p.Id == productId)).Unit);
    }

    /// <summary>
    /// A category of another business is not a category this product may point at, and it is refused
    /// with the same message as one that does not exist at all: the tenant query filters make the
    /// two indistinguishable, deliberately (AGENTS.md § Tenant ownership and data isolation). The
    /// request carries a category id, which is the only place a cross-business relationship could be
    /// attempted.
    /// </summary>
    [Fact]
    public async Task A_category_of_another_business_may_not_be_used_on_create_or_update()
    {
        long otherBusinessCategoryId;
        await using (var seed = TestAppDbContext.For(_options, BusinessB))
        {
            var category = new Category { Name = "B Drinks" };
            seed.Categories.Add(category);
            await seed.SaveChangesAsync();
            otherBusinessCategoryId = category.Id;
        }

        var productId = await SeedProductAsync(externalProductId: null, name: "Coke");

        await using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            var store = StoreFor(db);

            var created = await store.CreateAsync(
                CreateFields(categoryId: otherBusinessCategoryId), CancellationToken.None);
            Assert.Equal(ProductWriteOutcome.Refused, created.Outcome);
            Assert.Equal(ProductCataloguePolicy.UnknownCategoryMessage, created.Refusal);

            var updated = await store.UpdateAsync(
                productId,
                UpdateFields(categoryId: otherBusinessCategoryId),
                CancellationToken.None);
            Assert.Equal(ProductWriteOutcome.Refused, updated.Outcome);
            Assert.Equal(ProductCataloguePolicy.UnknownCategoryMessage, updated.Refusal);
        }

        await using var verify = TestAppDbContext.Unrestricted(_options);
        // Nothing was created, and the existing product was not pointed across the boundary.
        Assert.Equal(1, await verify.Products.CountAsync());
        Assert.Null((await verify.Products.AsNoTracking().SingleAsync(p => p.Id == productId)).CategoryId);
    }

    [Fact]
    public async Task An_unknown_category_may_not_be_used()
    {
        await using var db = TestAppDbContext.For(_options, BusinessA);

        var created = await StoreFor(db).CreateAsync(CreateFields(categoryId: 4242), CancellationToken.None);

        Assert.Equal(ProductWriteOutcome.Refused, created.Outcome);
        Assert.Equal(ProductCataloguePolicy.UnknownCategoryMessage, created.Refusal);
        Assert.Empty(await db.Products.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// Every kind of history refuses the delete, says so, and - this is the part only a relational
    /// test can show - leaves both the product and that history in the database. Two of these
    /// references cascade, so without the refusal the stock movement and the machine stock count
    /// would be gone.
    /// </summary>
    [Theory]
    [InlineData(HistoryKind.StockAdjustment, "1 stock movement")]
    [InlineData(HistoryKind.PurchaseLine, "1 purchase line")]
    [InlineData(HistoryKind.SupplierOrderLine, "1 supplier order line")]
    [InlineData(HistoryKind.Sale, "1 sale")]
    [InlineData(HistoryKind.MachineStockCount, "1 machine stock count")]
    [InlineData(HistoryKind.CostRepair, "1 costing record")]
    [InlineData(HistoryKind.CostTransitionBaseline, "1 costing record")]
    public async Task A_product_with_history_is_refused_and_keeps_both_the_product_and_the_history(
        HistoryKind kind, string expected)
    {
        var productId = await SeedProductAsync(externalProductId: null, name: "Coke");
        await SeedHistoryAsync(kind, productId);

        await using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            var attempt = await StoreFor(db).DeleteAsync(productId, CancellationToken.None);

            Assert.Equal(ProductWriteOutcome.Refused, attempt.Outcome);
            Assert.Contains(expected, attempt.Refusal!, StringComparison.Ordinal);
            Assert.Contains("Deactivate it instead", attempt.Refusal!, StringComparison.Ordinal);
        }

        await using var verify = TestAppDbContext.Unrestricted(_options);
        Assert.True(await verify.Products.AnyAsync(p => p.Id == productId));
        Assert.Equal(1, await CountHistoryAsync(verify, kind, productId));
    }

    /// <summary>
    /// The sale case the acceptance criteria call out: a sale that names the product only through its
    /// own persisted <c>ProductId</c> (issue #758), with no provider id and no name in common. The
    /// delete is refused over it, and the sale is untouched.
    /// </summary>
    [Fact]
    public async Task A_product_a_sale_names_only_by_its_persisted_product_id_is_refused()
    {
        var productId = await SeedProductAsync(externalProductId: null, name: "Coke");
        await using (var seed = TestAppDbContext.For(_options, BusinessA))
        {
            seed.NayaxSales.Add(new NayaxSales
            {
                TransactionID = 5001,
                MachineID = 10,
                MachineName = "Machine A",
                ProductId = productId,
                ProductName = "Something else entirely",
                SettlementValue = 3.50m,
                PaymentMethod = "Credit Card",
                MachineAuthorizationTime = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
                TransactionStatusId = NayaxTransactionStatusIds.Completed,
            });
            await seed.SaveChangesAsync();
        }

        await using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            var attempt = await StoreFor(db).DeleteAsync(productId, CancellationToken.None);

            Assert.Equal(ProductWriteOutcome.Refused, attempt.Outcome);
            Assert.Contains("1 sale", attempt.Refusal!, StringComparison.Ordinal);
        }

        await using var verify = TestAppDbContext.Unrestricted(_options);
        Assert.Equal(productId, (await verify.NayaxSales.AsNoTracking().SingleAsync()).ProductId);
    }

    /// <summary>
    /// A product the provider names is refused with no history at all: the next catalogue import
    /// would create it again.
    /// </summary>
    [Fact]
    public async Task A_provider_linked_product_may_not_be_deleted()
    {
        var productId = await SeedProductAsync(externalProductId: "100", name: "Nayax Coke");

        await using var db = TestAppDbContext.For(_options, BusinessA);
        var attempt = await StoreFor(db).DeleteAsync(productId, CancellationToken.None);

        Assert.Equal(ProductWriteOutcome.Refused, attempt.Outcome);
        Assert.Contains("managed by Nayax", attempt.Refusal!, StringComparison.Ordinal);
        Assert.True(await db.Products.AnyAsync(p => p.Id == productId));
    }

    /// <summary>
    /// Deactivating is the way to retire a product with history: the same update path writes
    /// <c>IsActive</c>, the history stays, and the product can be reactivated.
    /// </summary>
    [Fact]
    public async Task A_product_with_history_can_be_deactivated_and_reactivated()
    {
        var productId = await SeedProductAsync(externalProductId: null, name: "Coke");
        await SeedHistoryAsync(HistoryKind.StockAdjustment, productId);

        await using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            Assert.Equal(
                ProductWriteOutcome.Succeeded,
                (await StoreFor(db).UpdateAsync(
                    productId, UpdateFields(isActive: false), CancellationToken.None)).Outcome);
        }

        await using (var verify = TestAppDbContext.For(_options, BusinessA))
        {
            Assert.False((await verify.Products.AsNoTracking().SingleAsync(p => p.Id == productId)).IsActive);
            Assert.Equal(1, await verify.StockAdjustments.CountAsync(a => a.ProductId == productId));
        }

        await using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            Assert.Equal(
                ProductWriteOutcome.Succeeded,
                (await StoreFor(db).UpdateAsync(
                    productId, UpdateFields(isActive: true), CancellationToken.None)).Outcome);
        }

        await using var after = TestAppDbContext.For(_options, BusinessA);
        Assert.True((await after.Products.AsNoTracking().SingleAsync(p => p.Id == productId)).IsActive);
    }

    /// <summary>
    /// The sale-to-product foreign key stays restrictive - no cascade, no set-null - which is what
    /// makes the refusal above the only way a product a sale names can disappear (issue #758, and an
    /// explicit acceptance criterion of issue #736).
    /// </summary>
    [Fact]
    public void The_sale_to_product_foreign_key_stays_restrictive()
    {
        using var db = TestAppDbContext.Unrestricted(_options);

        var foreignKey = db.Model
            .FindEntityType(typeof(NayaxSales))!
            .GetForeignKeys()
            .Single(key => key.PrincipalEntityType.ClrType == typeof(Product));

        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
    }

    /// <summary>The kinds of history that refuse a product delete, one per counted reference.</summary>
    public enum HistoryKind
    {
        StockAdjustment,
        PurchaseLine,
        SupplierOrderLine,
        Sale,
        MachineStockCount,
        CostRepair,
        CostTransitionBaseline,
    }

    /// <summary>
    /// <paramref name="unit"/> is the stored unit label, which the unit-label tests of issue #895
    /// choose explicitly: a legacy product whose label was never set reads as a blank one, and the
    /// entity's own "unit" default would hide exactly that case.
    /// </summary>
    private async Task<long> SeedProductAsync(string? externalProductId, string name, string? unit = "can")
    {
        await using var seed = TestAppDbContext.For(_options, BusinessA);
        var product = new Product
        {
            Name = name,
            ExternalProductId = externalProductId,
            LowStockThreshold = 5,
            RestockTo = 10,
            Unit = unit,
        };
        seed.Products.Add(product);
        await seed.SaveChangesAsync();
        return product.Id;
    }

    private async Task SeedHistoryAsync(HistoryKind kind, long productId)
    {
        await using var seed = TestAppDbContext.For(_options, BusinessA);
        switch (kind)
        {
            case HistoryKind.StockAdjustment:
                seed.StockAdjustments.Add(new StockAdjustment
                {
                    ProductId = productId,
                    QuantityChange = 4,
                    QuantityAfter = 4,
                    Reason = StockAdjustmentReason.Restock,
                });
                break;
            case HistoryKind.PurchaseLine:
                seed.Receipts.Add(new Purchase
                {
                    Title = "January order",
                    PurchaseDate = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc),
                    Items = { new PurchaseItem { ProductId = productId, Quantity = 10, UnitCost = 2m } },
                });
                break;
            case HistoryKind.SupplierOrderLine:
                seed.SupplierOrders.Add(new SupplierOrder
                {
                    SupplierId = null,
                    Lines = { new SupplierOrderLine { ProductId = productId, QuantityOrdered = 8 } },
                });
                break;
            case HistoryKind.Sale:
                seed.NayaxSales.Add(new NayaxSales
                {
                    TransactionID = 9001,
                    MachineID = 10,
                    MachineName = "Machine A",
                    ProductId = productId,
                    SettlementValue = 3.50m,
                    PaymentMethod = "Credit Card",
                    MachineAuthorizationTime = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
                    TransactionStatusId = NayaxTransactionStatusIds.Completed,
                });
                break;
            case HistoryKind.MachineStockCount:
                // A count names a local machine (issue #741), so the machine exists.
                seed.Machines.Add(new LocalMachine { Id = 10, Name = "Machine A" });
                seed.MachineStockCounts.Add(new MachineStockCount
                {
                    MachineId = 10,
                    ProductId = productId,
                    CountedQuantity = 6,
                    CountedAtUtc = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
                });
                break;
            case HistoryKind.CostRepair:
                seed.InventoryCostRepairs.Add(new InventoryCostRepair
                {
                    ProductId = productId,
                    Quantity = 3,
                    UnitCost = 1.50m,
                    TotalValue = 4.50m,
                    Reason = "Never recorded",
                    EffectiveAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
                    CreatedAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
                    CreatedByDirectoryTenantId = "tenant",
                    CreatedByObjectId = "object",
                });
                break;
            case HistoryKind.CostTransitionBaseline:
                seed.InventoryCostTransitionBaselines.Add(new InventoryCostTransitionBaseline
                {
                    ProductId = productId,
                    CutoffAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    OpeningCostingQuantity = 5,
                    AverageUnitCost = 1m,
                    InventoryValue = 5m,
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled history kind.");
        }

        await seed.SaveChangesAsync();
    }

    private static Task<int> CountHistoryAsync(AppDbContext db, HistoryKind kind, long productId) => kind switch
    {
        HistoryKind.StockAdjustment =>
            db.StockAdjustments.CountAsync(adjustment => adjustment.ProductId == productId),
        HistoryKind.PurchaseLine => db.ReceiptItems.CountAsync(item => item.ProductId == productId),
        HistoryKind.SupplierOrderLine => db.SupplierOrderLines.CountAsync(line => line.ProductId == productId),
        HistoryKind.Sale => db.NayaxSales.CountAsync(sale => sale.ProductId == productId),
        HistoryKind.MachineStockCount => db.MachineStockCounts.CountAsync(count => count.ProductId == productId),
        HistoryKind.CostRepair => db.InventoryCostRepairs.CountAsync(repair => repair.ProductId == productId),
        HistoryKind.CostTransitionBaseline =>
            db.InventoryCostTransitionBaselines.CountAsync(baseline => baseline.ProductId == productId),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled history kind."),
    };

    /// <summary>
    /// A cost rebuild that fails, for the fault injection above. It is deliberately the whole port:
    /// a create that reached any other method of it would be doing something this test is not
    /// describing, and would fail here rather than pass quietly.
    /// </summary>
    private sealed class FailingRebuild(Exception failure) : IRebuildProductCost
    {
        public Task<InventoryCostRebuildResult> RebuildAsync(
            long productId,
            DateTime? recostCompletedSalesFrom = null,
            bool dryRun = false,
            CancellationToken cancellationToken = default) => throw failure;

        public Task<InventoryCostRebuildResult> RebuildCostingOnlyAsync(
            long productId,
            DateTime recostCompletedSalesFrom,
            CancellationToken cancellationToken = default) => throw failure;

        public Task<decimal?> GetAverageUnitCostAtAsync(
            long productId,
            DateTime saleTime,
            long? saleTransactionId = null,
            CancellationToken cancellationToken = default) => throw failure;
    }
}
