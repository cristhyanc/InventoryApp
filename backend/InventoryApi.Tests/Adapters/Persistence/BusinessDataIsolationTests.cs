using InventoryApi.Data;
using InventoryApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The relational proof that the tenant boundary holds (issue #64): two businesses share one
/// database, and neither can read, change, delete, or relate to the other's records.
///
/// These run on SQLite, not InMemory, because the whole point is what the real query pipeline
/// and real SQL do - a global query filter that InMemory happened to honour would prove nothing
/// about the database the application actually runs on.
///
/// Business A is 1 and business B is 2 throughout.
/// </summary>
public class BusinessDataIsolationTests : IDisposable
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public BusinessDataIsolationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
        setup.Businesses.AddRange(
            new Business { Id = BusinessA, Name = "Vending A", CreatedAtUtc = DateTime.UtcNow },
            new Business { Id = BusinessB, Name = "Vending B", CreatedAtUtc = DateTime.UtcNow });
        setup.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>A context acting as the given business, exactly as a scoped request would.</summary>
    private AppDbContext AsBusiness(int businessId) => TestAppDbContext.For(_options, businessId);

    /// <summary>A context for a caller with no resolvable business membership.</summary>
    private AppDbContext AsDeniedCaller() => TestAppDbContext.Denied(_options);

    /// <summary>
    /// Seeds one business's records without going through the scoped path, so a test can set up
    /// the *other* business's data as an unrelated fact of the world.
    /// </summary>
    private void SeedFor(int businessId, Action<AppDbContext, int> seed)
    {
        using var db = TestAppDbContext.Unrestricted(_options);
        seed(db, businessId);
        db.SaveChanges();
    }

    private static Product NewProduct(int businessId, string name) => new()
    {
        BusinessId = businessId,
        Name = name,
        Sku = $"{name}-SKU",
        UnitPrice = 2.50m,
        QuantityInStock = 5,
    };

    private static Supplier NewSupplier(int businessId, string name) =>
        new() { BusinessId = businessId, Name = name };

    #region Reads

    /// <summary>
    /// The headline property: every tenant-owned DbSet is filtered, including the child
    /// collections whose ownership is structurally implied by a parent. Children have their own
    /// DbSets, and EF does not apply a parent's filter to a direct child query, so each one
    /// carries ownership of its own.
    /// </summary>
    [Fact]
    public void Business_B_records_are_invisible_to_business_A_across_every_tenant_owned_set()
    {
        SeedFor(BusinessB, (db, business) =>
        {
            var product = NewProduct(business, "B Product");

            db.Categories.Add(new Category { BusinessId = business, Name = "B Category" });
            db.Suppliers.Add(NewSupplier(business, "B Supplier"));
            db.Products.Add(product);
            db.StockAdjustments.Add(new StockAdjustment { BusinessId = business, Product = product, QuantityChange = 1 });
            db.Receipts.Add(new Purchase { BusinessId = business, Title = "B Purchase" });
            db.NayaxSales.Add(new NayaxSales { BusinessId = business, TransactionID = 99, MachineID = 5, SettlementValue = 4m });
            db.OperatingExpenses.Add(new OperatingExpense { BusinessId = business, Description = "B Expense" });
            db.NayaxProcessingFeeRates.Add(new NayaxProcessingFeeRate { BusinessId = business, FeeExGst = 0.2m });
            db.SiteCommissionAgreements.Add(new SiteCommissionAgreement { BusinessId = business, SiteId = 7, CommissionRate = 0.1m });
            db.CommissionPayments.Add(new CommissionPayment { BusinessId = business, SiteId = 7, Amount = 5m });
            db.SupplierOrders.Add(new SupplierOrder { BusinessId = business });
            db.ImportedFiles.Add(new ImportedFile { BusinessId = business, FileName = "b.xml", FileHash = "b-hash" });
            db.InventoryCostTransitionPreviewDrafts.Add(new InventoryCostTransitionPreviewDraft
            {
                BusinessId = business,
                Id = Guid.NewGuid(),
                ProductId = product.Id,
            });
        });

        using var a = AsBusiness(BusinessA);

        Assert.Empty(a.Categories.ToList());
        Assert.Empty(a.Suppliers.ToList());
        Assert.Empty(a.Products.ToList());
        Assert.Empty(a.StockAdjustments.ToList());
        Assert.Empty(a.Receipts.ToList());
        Assert.Empty(a.NayaxSales.ToList());
        Assert.Empty(a.OperatingExpenses.ToList());
        Assert.Empty(a.NayaxProcessingFeeRates.ToList());
        Assert.Empty(a.SiteCommissionAgreements.ToList());
        Assert.Empty(a.CommissionPayments.ToList());
        Assert.Empty(a.SupplierOrders.ToList());
        Assert.Empty(a.ImportedFiles.ToList());
        Assert.Empty(a.InventoryCostTransitionPreviewDrafts.ToList());
    }

    [Fact]
    public void Each_business_sees_only_its_own_records_when_both_have_data()
    {
        SeedFor(BusinessA, (db, business) => db.Products.Add(NewProduct(business, "A Product")));
        SeedFor(BusinessB, (db, business) => db.Products.Add(NewProduct(business, "B Product")));

        using var a = AsBusiness(BusinessA);
        using var b = AsBusiness(BusinessB);

        Assert.Equal("A Product", Assert.Single(a.Products.ToList()).Name);
        Assert.Equal("B Product", Assert.Single(b.Products.ToList()).Name);
    }

    /// <summary>
    /// Looking a record up by its primary key is the classic cross-tenant hole: the id is known,
    /// so nothing in the request itself looks suspicious. It must come back as not-found.
    /// </summary>
    [Fact]
    public void A_record_of_another_business_cannot_be_fetched_by_its_id()
    {
        SeedFor(BusinessB, (db, business) => db.Products.Add(NewProduct(business, "B Product")));

        long bProductId;
        using (var seeded = TestAppDbContext.Unrestricted(_options))
        {
            bProductId = seeded.Products.Single().Id;
        }

        using var a = AsBusiness(BusinessA);

        Assert.Null(a.Products.FirstOrDefault(p => p.Id == bProductId));
        Assert.Null(a.Products.Find(bProductId));
    }

    /// <summary>
    /// A caller with no resolvable membership sees nothing at all. Failing closed matters most
    /// here: the tempting bug is to treat "no current business" as "do not filter".
    /// </summary>
    [Fact]
    public void A_caller_without_a_resolved_business_reads_nothing()
    {
        SeedFor(BusinessA, (db, business) => db.Products.Add(NewProduct(business, "A Product")));
        SeedFor(BusinessB, (db, business) => db.Products.Add(NewProduct(business, "B Product")));

        using var denied = AsDeniedCaller();

        Assert.Empty(denied.Products.ToList());
        Assert.Empty(denied.Categories.ToList());
        Assert.Empty(denied.Receipts.ToList());
    }

    /// <summary>
    /// An Include must not become a side door into another business's rows.
    /// </summary>
    [Fact]
    public void Related_records_of_another_business_are_not_reachable_through_an_include()
    {
        SeedFor(BusinessB, (db, business) =>
        {
            var product = NewProduct(business, "B Product");
            db.Products.Add(product);

            var purchase = new Purchase { BusinessId = business, Title = "B Purchase" };
            purchase.Items.Add(new PurchaseItem { BusinessId = business, Product = product, Quantity = 1, UnitCost = 1m });
            db.Receipts.Add(purchase);
        });

        using var a = AsBusiness(BusinessA);

        Assert.Empty(a.Receipts.Include(p => p.Items).ToList());
        Assert.Empty(a.ReceiptItems.ToList());
    }

    #endregion

    #region Writes

    [Fact]
    public async Task A_new_record_is_stamped_with_the_callers_business_without_being_asked()
    {
        await using (var a = AsBusiness(BusinessA))
        {
            // Note: BusinessId is deliberately not set by the caller.
            a.Products.Add(new Product { Name = "A Product", Sku = "A-1", UnitPrice = 1m });
            await a.SaveChangesAsync();
        }

        await using var verify = TestAppDbContext.Unrestricted(_options);
        Assert.Equal(BusinessA, verify.Products.Single().BusinessId);
    }

    /// <summary>
    /// Supplying another business's id on insert must be refused rather than honoured - this is
    /// the "never take the business id from client input" rule, enforced at the last gate.
    /// </summary>
    [Fact]
    public async Task Creating_a_record_owned_by_another_business_is_refused()
    {
        await using var a = AsBusiness(BusinessA);
        a.Products.Add(NewProduct(BusinessB, "Smuggled Product"));

        await Assert.ThrowsAsync<CrossBusinessAccessException>(() => a.SaveChangesAsync());
    }

    /// <summary>
    /// A detached update is the write-side equivalent of the by-id read: the row is never loaded
    /// through the filtered query, so only the write gate can stop it.
    /// </summary>
    [Fact]
    public async Task Updating_another_business_record_attached_by_id_is_refused()
    {
        SeedFor(BusinessB, (db, business) => db.Products.Add(NewProduct(business, "B Product")));

        long bProductId;
        await using (var seeded = TestAppDbContext.Unrestricted(_options))
        {
            bProductId = seeded.Products.Single().Id;
        }

        await using var a = AsBusiness(BusinessA);
        var detached = new Product { Id = bProductId, BusinessId = BusinessB, Name = "Hijacked", UnitPrice = 999m };
        a.Products.Attach(detached);
        a.Entry(detached).State = EntityState.Modified;

        await Assert.ThrowsAsync<CrossBusinessAccessException>(() => a.SaveChangesAsync());

        await using var verify = TestAppDbContext.Unrestricted(_options);
        Assert.Equal("B Product", verify.Products.Single().Name);
    }

    [Fact]
    public async Task Deleting_another_business_record_attached_by_id_is_refused()
    {
        SeedFor(BusinessB, (db, business) => db.Products.Add(NewProduct(business, "B Product")));

        long bProductId;
        await using (var seeded = TestAppDbContext.Unrestricted(_options))
        {
            bProductId = seeded.Products.Single().Id;
        }

        await using var a = AsBusiness(BusinessA);
        a.Products.Remove(new Product { Id = bProductId, BusinessId = BusinessB });

        await Assert.ThrowsAsync<CrossBusinessAccessException>(() => a.SaveChangesAsync());

        await using var verify = TestAppDbContext.Unrestricted(_options);
        Assert.Single(verify.Products.ToList());
    }

    /// <summary>
    /// Ownership is immutable: a business cannot give its own record away, which would otherwise
    /// be a one-line path to moving data between tenants.
    /// </summary>
    [Fact]
    public async Task Reassigning_an_owned_record_to_another_business_is_refused()
    {
        SeedFor(BusinessA, (db, business) => db.Products.Add(NewProduct(business, "A Product")));

        await using var a = AsBusiness(BusinessA);
        var product = a.Products.Single();
        product.BusinessId = BusinessB;

        await Assert.ThrowsAsync<CrossBusinessAccessException>(() => a.SaveChangesAsync());
    }

    [Fact]
    public async Task A_caller_without_a_resolved_business_cannot_write()
    {
        await using var denied = AsDeniedCaller();
        denied.Products.Add(new Product { Name = "Orphan", UnitPrice = 1m });

        await Assert.ThrowsAsync<CrossBusinessAccessException>(() => denied.SaveChangesAsync());
    }

    #endregion

    #region Cross-business relationships

    /// <summary>
    /// The relationship rule the issue calls out by name: a product must not be able to point at
    /// another business's supplier. The foreign key value is valid and the row exists - only the
    /// ownership check distinguishes it from a legitimate reference.
    /// </summary>
    [Fact]
    public async Task A_product_cannot_reference_a_supplier_from_another_business()
    {
        SeedFor(BusinessB, (db, business) => db.Suppliers.Add(NewSupplier(business, "B Supplier")));

        int bSupplierId;
        await using (var seeded = TestAppDbContext.Unrestricted(_options))
        {
            bSupplierId = seeded.Suppliers.Single().Id;
        }

        await using var a = AsBusiness(BusinessA);
        a.Products.Add(new Product { Name = "A Product", UnitPrice = 1m, SupplierId = bSupplierId });

        var exception = await Assert.ThrowsAsync<CrossBusinessAccessException>(() => a.SaveChangesAsync());
        Assert.Contains("Supplier", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_product_cannot_reference_a_category_from_another_business()
    {
        SeedFor(BusinessB, (db, business) => db.Categories.Add(new Category { BusinessId = business, Name = "B Category" }));

        long bCategoryId;
        await using (var seeded = TestAppDbContext.Unrestricted(_options))
        {
            bCategoryId = seeded.Categories.Single().Id;
        }

        await using var a = AsBusiness(BusinessA);
        a.Products.Add(new Product { Name = "A Product", UnitPrice = 1m, CategoryId = bCategoryId });

        await Assert.ThrowsAsync<CrossBusinessAccessException>(() => a.SaveChangesAsync());
    }

    [Fact]
    public async Task A_purchase_item_cannot_reference_a_product_from_another_business()
    {
        SeedFor(BusinessB, (db, business) => db.Products.Add(NewProduct(business, "B Product")));

        long bProductId;
        await using (var seeded = TestAppDbContext.Unrestricted(_options))
        {
            bProductId = seeded.Products.Single().Id;
        }

        await using var a = AsBusiness(BusinessA);
        var purchase = new Purchase { Title = "A Purchase" };
        purchase.Items.Add(new PurchaseItem { ProductId = bProductId, Quantity = 1, UnitCost = 1m });
        a.Receipts.Add(purchase);

        await Assert.ThrowsAsync<CrossBusinessAccessException>(() => a.SaveChangesAsync());
    }

    [Fact]
    public async Task A_supplier_order_cannot_reference_a_supplier_from_another_business()
    {
        SeedFor(BusinessB, (db, business) => db.Suppliers.Add(NewSupplier(business, "B Supplier")));

        int bSupplierId;
        await using (var seeded = TestAppDbContext.Unrestricted(_options))
        {
            bSupplierId = seeded.Suppliers.Single().Id;
        }

        await using var a = AsBusiness(BusinessA);
        a.SupplierOrders.Add(new SupplierOrder { SupplierId = bSupplierId });

        await Assert.ThrowsAsync<CrossBusinessAccessException>(() => a.SaveChangesAsync());
    }

    [Fact]
    public async Task An_operating_expense_cannot_reference_a_supplier_from_another_business()
    {
        SeedFor(BusinessB, (db, business) => db.Suppliers.Add(NewSupplier(business, "B Supplier")));

        int bSupplierId;
        await using (var seeded = TestAppDbContext.Unrestricted(_options))
        {
            bSupplierId = seeded.Suppliers.Single().Id;
        }

        await using var a = AsBusiness(BusinessA);
        a.OperatingExpenses.Add(new OperatingExpense { Description = "A Expense", SupplierId = bSupplierId });

        await Assert.ThrowsAsync<CrossBusinessAccessException>(() => a.SaveChangesAsync());
    }

    [Fact]
    public async Task A_stock_adjustment_cannot_reference_a_product_from_another_business()
    {
        SeedFor(BusinessB, (db, business) => db.Products.Add(NewProduct(business, "B Product")));

        long bProductId;
        await using (var seeded = TestAppDbContext.Unrestricted(_options))
        {
            bProductId = seeded.Products.Single().Id;
        }

        await using var a = AsBusiness(BusinessA);
        a.StockAdjustments.Add(new StockAdjustment { ProductId = bProductId, QuantityChange = -1 });

        await Assert.ThrowsAsync<CrossBusinessAccessException>(() => a.SaveChangesAsync());
    }

    /// <summary>
    /// An update that repoints an existing record at another business's record is the same
    /// violation arriving by a different route, so it must be refused too.
    /// </summary>
    [Fact]
    public async Task Repointing_an_existing_record_at_another_business_is_refused()
    {
        SeedFor(BusinessA, (db, business) => db.Products.Add(NewProduct(business, "A Product")));
        SeedFor(BusinessB, (db, business) => db.Suppliers.Add(NewSupplier(business, "B Supplier")));

        int bSupplierId;
        await using (var seeded = TestAppDbContext.Unrestricted(_options))
        {
            bSupplierId = seeded.Suppliers.Single().Id;
        }

        await using var a = AsBusiness(BusinessA);
        a.Products.Single().SupplierId = bSupplierId;

        await Assert.ThrowsAsync<CrossBusinessAccessException>(() => a.SaveChangesAsync());
    }

    /// <summary>
    /// The mirror of the rejection tests: a relationship inside one business must still work,
    /// so the check is not simply refusing everything.
    /// </summary>
    [Fact]
    public async Task A_relationship_within_one_business_is_allowed()
    {
        await using (var a = AsBusiness(BusinessA))
        {
            var supplier = NewSupplier(BusinessA, "A Supplier");
            supplier.BusinessId = 0;
            a.Suppliers.Add(supplier);
            await a.SaveChangesAsync();

            a.Products.Add(new Product { Name = "A Product", UnitPrice = 1m, SupplierId = supplier.Id });
            await a.SaveChangesAsync();
        }

        await using var verify = AsBusiness(BusinessA);
        var product = verify.Products.Single();
        Assert.Equal(BusinessA, product.BusinessId);
        Assert.Equal(verify.Suppliers.Single().Id, product.SupplierId);
    }

    /// <summary>
    /// A parent and its children inserted together in one save must all be stamped and accepted:
    /// the relationship check has to consult the change tracker, not only the database, or a
    /// legitimate new aggregate would be rejected because its parent is not persisted yet.
    /// </summary>
    [Fact]
    public async Task A_new_aggregate_saved_in_one_call_is_stamped_throughout()
    {
        await using (var a = AsBusiness(BusinessA))
        {
            var product = new Product { Name = "A Product", UnitPrice = 1m };
            a.Products.Add(product);

            var purchase = new Purchase { Title = "A Purchase" };
            purchase.Items.Add(new PurchaseItem { Product = product, Quantity = 2, UnitCost = 1m });
            a.Receipts.Add(purchase);

            await a.SaveChangesAsync();
        }

        await using var verify = TestAppDbContext.Unrestricted(_options);
        Assert.Equal(BusinessA, verify.Receipts.Single().BusinessId);
        Assert.Equal(BusinessA, verify.ReceiptItems.Single().BusinessId);
        Assert.Equal(BusinessA, verify.Products.Single().BusinessId);
    }

    #endregion
}
