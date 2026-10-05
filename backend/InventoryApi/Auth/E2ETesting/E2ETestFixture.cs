using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Auth.E2ETesting;

/// <summary>
/// The deterministic, isolated test data the browser-level end-to-end suite runs against
/// (issue #46).
///
/// It is seeded once at startup, and only in the dedicated E2E host - the same
/// <see cref="E2ETestEnvironment"/> gate the synthetic authentication scheme is behind - against
/// the disposable SQLite database the harness points that host at. It is not a migration, not a
/// backfill, and never touches an existing row: it creates the two synthetic businesses, their
/// memberships, and a small catalogue, and it stops as soon as it finds its own data already
/// there, so restarting the host is safe and a second run adds nothing.
///
/// Two properties of this seeder are deliberate and should stay that way:
///
/// <list type="bullet">
///   <item><b>It never runs unscoped.</b> <c>Business</c> and <c>BusinessMembership</c> are not
///   tenant-owned rows, so they are written through an ordinary fail-closed context. Every
///   tenant-owned row is written through a <see cref="BusinessScope"/> resolved to exactly one
///   business, which is the same mechanism a request uses, so
///   <c>BusinessOwnershipEnforcer</c> stamps and checks ownership here too.
///   <c>UnscopedBusinessScope</c> stays reserved for the three human-invoked commands
///   (AGENTS.md § Tenant ownership and data isolation).</item>
///   <item><b>It fabricates no costing history.</b> The products start with no costing quantity
///   and no average cost, and the one seeded Nayax sale is deliberately left uncosted, which is
///   what gives the suite a genuine "COGS and profit are unavailable" report state to assert on
///   instead of a zero.</item>
/// </list>
///
/// The names below are the contract between this fixture and the Playwright suite in
/// <c>frontend/inventory-app/e2e</c>; changing one means changing both.
/// </summary>
public static class E2ETestFixture
{
    /// <summary>A fixed instant, so a run in October reads exactly like a run in March.</summary>
    public static readonly DateTime SeededAtUtc = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The authorization instant of the seeded uncosted sale. 03:00 UTC is mid-afternoon in the
    /// <c>Australia/Sydney</c> business timezone, so the UTC instant and the business date the
    /// reports group it by name the same day whatever the host timezone is.
    /// </summary>
    public static readonly DateTime UncostedSaleAuthorizedAtUtc = new(2026, 3, 2, 3, 0, 0, DateTimeKind.Utc);

    public const string BusinessAName = "E2E Business A";
    public const string BusinessBName = "E2E Business B";

    /// <summary>Below its low-stock threshold, so it is a reorder alert on a fresh database.</summary>
    public const string ReorderProductName = "E2E Reorder Chips";

    /// <summary>Comfortably in stock, for the positive-magnitude negative stock correction.</summary>
    public const string CorrectionProductName = "E2E Correction Bars";

    /// <summary>Starts at zero stock and zero costing quantity, for the purchase smoke test.</summary>
    public const string PurchaseProductName = "E2E Purchase Water";

    /// <summary>Business B's only product: the row Business A must never be able to see.</summary>
    public const string BusinessBProductName = "E2E Business B Chocolate";

    /// <summary>
    /// The product name on the seeded uncosted sale. It deliberately matches no product in the
    /// catalogue, so the cost rebuild can never match the sale to one
    /// (<c>Inventory.Domain.Reporting.ProductMatching.ProductMatcher</c> matches a sale by Nayax
    /// product id or by product name). That keeps the sale permanently uncosted whatever the suite
    /// does to the catalogue, which is what makes the "COGS and profit unavailable" report state
    /// deterministic rather than dependent on which test ran first. An unmapped completed sale is
    /// also a real data-quality state this application already reports.
    /// </summary>
    public const string UncostedSaleProductName = "E2E Unmapped Vended Item";

    public const string SupplierAName = "E2E Supplier A";
    public const string SupplierBName = "E2E Supplier B";
    public const string CategoryName = "E2E Snacks";

    /// <summary>The settlement value of the seeded uncosted completed card sale.</summary>
    public const decimal UncostedSaleAmount = 5.50m;

    private const long SeededMachineId = 460001;
    private const long UncostedSaleTransactionIdA = 4600001;

    /// <summary>
    /// Seeds the fixture, or does nothing at all outside the dedicated E2E host.
    ///
    /// The environment check is repeated here rather than left to the caller: this method writes
    /// data, so it must be unable to do so in the wrong host even if it is called from the wrong
    /// place.
    /// </summary>
    public static async Task SeedAsync(
        IServiceProvider services,
        IHostEnvironment environment,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);

        if (!E2ETestEnvironment.IsEnabled(environment))
        {
            return;
        }

        var options = services.GetRequiredService<DbContextOptions<AppDbContext>>();

        int businessAId;
        int businessBId;

        // Neither a business nor a membership is tenant-owned, so an ordinary (fail-closed)
        // context creates them. It is still not unrestricted: a tenant-owned write through this
        // context would be refused by the ownership enforcer.
        await using (var db = new AppDbContext(options))
        {
            businessAId = await EnsureBusinessAsync(db, BusinessAName, E2ETestActors.BusinessAOwner, cancellationToken);
            businessBId = await EnsureBusinessAsync(db, BusinessBName, E2ETestActors.BusinessBOwner, cancellationToken);
        }

        await SeedBusinessAAsync(options, businessAId, cancellationToken);
        await SeedBusinessBAsync(options, businessBId, cancellationToken);

        // No Entra identifier, connection string or file path is logged - only that the fixture
        // is in place, which is what an operator looking at an E2E run needs to know.
        logger.LogInformation(
            "E2E test fixture ready: two synthetic businesses with memberships and catalogue data.");
    }

    /// <summary>
    /// Creates the business and its single membership when they are missing, and leaves both
    /// alone when they already exist. The membership is what makes the synthetic actor an
    /// authorised caller; <see cref="E2ETestActors.NoMembership"/> deliberately never gets one.
    /// </summary>
    private static async Task<int> EnsureBusinessAsync(
        AppDbContext db,
        string name,
        E2ETestActor owner,
        CancellationToken cancellationToken)
    {
        var business = await db.Businesses.FirstOrDefaultAsync(b => b.Name == name, cancellationToken);

        if (business is null)
        {
            business = new Business
            {
                Name = name,
                IsActive = true,
                CreatedAtUtc = SeededAtUtc,
            };

            db.Businesses.Add(business);
            await db.SaveChangesAsync(cancellationToken);
        }

        var hasMembership = await db.BusinessMemberships.AnyAsync(
            membership => membership.BusinessId == business.Id
                && membership.DirectoryTenantId == owner.DirectoryTenantId
                && membership.ObjectId == owner.ObjectId,
            cancellationToken);

        if (!hasMembership)
        {
            db.BusinessMemberships.Add(new BusinessMembership
            {
                BusinessId = business.Id,
                DirectoryTenantId = owner.DirectoryTenantId,
                ObjectId = owner.ObjectId,
                IsActive = true,
                CreatedAtUtc = SeededAtUtc,
            });

            await db.SaveChangesAsync(cancellationToken);
        }

        return business.Id;
    }

    private static async Task SeedBusinessAAsync(
        DbContextOptions<AppDbContext> options,
        int businessId,
        CancellationToken cancellationToken)
    {
        await using var db = ScopedContext(options, businessId);

        if (await db.Products.AnyAsync(cancellationToken))
        {
            return;
        }

        var category = new Category { Name = CategoryName, Description = "Seeded E2E category" };
        var supplier = new Supplier { Name = SupplierAName, ContactName = "E2E Contact", Email = "supplier-a@e2e.invalid" };
        db.Categories.Add(category);
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync(cancellationToken);

        // Quantities, thresholds and restock targets are chosen so the reorder page has exactly
        // one alerting product on a fresh database: 2 in stock at a threshold of 10 needs
        // 30 - 2 = 28 ordered, while the other two products need nothing.
        var reorderProduct = NewProduct(ReorderProductName, "E2E-REORDER", category, supplier,
            quantityInStock: 2, lowStockThreshold: 10, restockTo: 30, unitPrice: 3.50m, openingUnitCost: 1.20m);
        var correctionProduct = NewProduct(CorrectionProductName, "E2E-CORRECTION", category, supplier,
            quantityInStock: 20, lowStockThreshold: 5, restockTo: 24, unitPrice: 4.00m, openingUnitCost: 2.00m);
        var purchaseProduct = NewProduct(PurchaseProductName, "E2E-PURCHASE", category, supplier,
            quantityInStock: 0, lowStockThreshold: 0, restockTo: 0, unitPrice: 2.50m, openingUnitCost: null);
        db.Products.AddRange(reorderProduct, correctionProduct, purchaseProduct);
        await db.SaveChangesAsync(cancellationToken);

        // Opening stock is a recorded, costed restock movement, not a quantity that appeared from
        // nowhere. The cost replay reconstructs physical stock, costing quantity and AVCO from the
        // movement history, so a stored quantity with no movement behind it would be an
        // inconsistent starting point: the first correction or sale against such a product goes
        // physically negative with no known cost, and the API refuses it as a data-quality fault.
        // Seeding the movement puts the fixture in the shape the application itself produces.
        db.StockAdjustments.AddRange(OpeningStock(reorderProduct), OpeningStock(correctionProduct));
        await db.SaveChangesAsync(cancellationToken);

        // One completed card sale with no cost of its own: no persisted AVCO cost, no Nayax
        // transaction cost price, and no catalogue product it can be matched to, so it stays
        // uncosted and the reports must report COGS and profit as unavailable rather than zero.
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = UncostedSaleTransactionIdA,
            TransactionStatusId = 12,
            MachineID = SeededMachineId,
            MachineName = "E2E Machine 1",
            SettlementValue = UncostedSaleAmount,
            PaymentMethod = "Credit Card",
            ProductName = UncostedSaleProductName,
            MachineAuthorizationTime = UncostedSaleAuthorizedAtUtc,
            NayaxProductCostPrice = null,
            UnitCostAtSale = null,
            CostOfGoodsSold = null,
            // Pending/Unknown is the honest state of a sale nothing has costed: the reports decide
            // a sale is uncosted from the absent cost of goods, not from this status.
            CostingStatus = SaleCostingStatus.Pending,
            CostSource = SaleCostSource.Unknown,
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task SeedBusinessBAsync(
        DbContextOptions<AppDbContext> options,
        int businessId,
        CancellationToken cancellationToken)
    {
        await using var db = ScopedContext(options, businessId);

        if (await db.Products.AnyAsync(cancellationToken))
        {
            return;
        }

        var category = new Category { Name = CategoryName, Description = "Seeded E2E category" };
        var supplier = new Supplier { Name = SupplierBName, ContactName = "E2E Contact", Email = "supplier-b@e2e.invalid" };
        db.Categories.Add(category);
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync(cancellationToken);

        var product = NewProduct(BusinessBProductName, "E2E-OTHER", category, supplier,
            quantityInStock: 50, lowStockThreshold: 5, restockTo: 60, unitPrice: 5.00m, openingUnitCost: 2.50m);
        db.Products.Add(product);
        await db.SaveChangesAsync(cancellationToken);

        db.StockAdjustments.Add(OpeningStock(product));
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// A context scoped to one business, exactly as the business-scope middleware produces for a
    /// request. Direct construction is confined to this startup seeder; a request path resolves
    /// its context from dependency injection.
    /// </summary>
    private static AppDbContext ScopedContext(DbContextOptions<AppDbContext> options, int businessId)
    {
        var scope = new BusinessScope();
        scope.Resolve(BusinessId.From(businessId));

        return new AppDbContext(options, scope);
    }

    /// <summary>
    /// One seeded product. <paramref name="openingUnitCost"/> is the cost its opening stock was
    /// acquired at - the costing position below is exactly that quantity at that cost, and
    /// <see cref="OpeningStock"/> records the movement it came from. A product with no opening
    /// stock passes <c>null</c> and starts with nothing: no quantity, no costing quantity, and no
    /// average cost to be mistaken for one.
    /// </summary>
    private static Product NewProduct(
        string name,
        string sku,
        Category category,
        Supplier supplier,
        int quantityInStock,
        int lowStockThreshold,
        int restockTo,
        decimal unitPrice,
        decimal? openingUnitCost = null) =>
        new()
        {
            Name = name,
            Sku = sku,
            Description = "Seeded E2E product",
            Category = category,
            Supplier = supplier,
            QuantityInStock = quantityInStock,
            LowStockThreshold = lowStockThreshold,
            RestockTo = restockTo,
            UnitPrice = unitPrice,
            Unit = "unit",
            IsActive = true,
            AverageUnitCost = openingUnitCost ?? 0m,
            CostingQuantity = openingUnitCost is null ? 0 : quantityInStock,
            InventoryValue = openingUnitCost is null ? 0m : quantityInStock * openingUnitCost.Value,
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };

    /// <summary>
    /// The restock movement the product's opening stock came from, consistent with the costing
    /// position stored on the product itself: same quantity, same unit cost, same resulting value.
    /// </summary>
    private static StockAdjustment OpeningStock(Product product) => new()
    {
        Product = product,
        QuantityChange = product.QuantityInStock,
        QuantityAfter = product.QuantityInStock,
        UnitCost = product.AverageUnitCost,
        TotalCost = product.QuantityInStock * product.AverageUnitCost,
        CostingQuantityAfter = product.CostingQuantity,
        AverageUnitCostAfter = product.AverageUnitCost,
        InventoryValueAfter = product.InventoryValue,
        Reason = StockAdjustmentReason.Restock,
        Source = StockAdjustmentSource.Manual,
        Notes = "Seeded E2E opening stock",
        CreatedAt = SeededAtUtc,
        EffectiveAt = SeededAtUtc,
    };
}
