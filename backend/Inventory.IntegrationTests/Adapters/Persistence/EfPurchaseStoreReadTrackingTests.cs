using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Issue #1289 (audit #1226, finding DB-04): <see cref="EfPurchaseStore.ListAsync"/> and
/// <see cref="EfPurchaseStore.FindByIdAsync"/> are pure reads of a purchase with its supplier, its
/// lines and each line's product, so nothing they load should be left in the context's change
/// tracker, and the request's own cancellation token should reach the database call rather than
/// being replaced with one that can never fire. Relational SQLite is what can prove either claim:
/// the EF Core InMemory provider never really tracks or cancels a query the way a relational
/// provider does.
/// </summary>
public sealed class EfPurchaseStoreReadTrackingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public EfPurchaseStoreReadTrackingTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>A purchase with a supplier, several lines and a stored document - the shape the acceptance criterion names.</summary>
    private async Task<int> SeedPurchaseWithLinesSupplierAndDocumentAsync()
    {
        await using var seed = TestAppDbContext.Unrestricted(_options);
        var product1 = new Product { Name = "Coke" };
        var product2 = new Product { Name = "Chips" };
        seed.Products.AddRange(product1, product2);
        var supplier = new Supplier { Name = "Acme" };
        seed.Suppliers.Add(supplier);

        var purchase = new Purchase
        {
            Title = "Weekly restock",
            TotalAmount = 30m,
            PurchaseDate = new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc),
            FileName = "scan.jpg",
            StoredFileName = "abc-def.jpg",
            ContentType = "image/jpeg",
            FileSizeBytes = 3,
            Supplier = supplier,
        };
        purchase.Items.Add(new PurchaseItem { Product = product1, Quantity = 24m, UnitCost = 1.15m });
        purchase.Items.Add(new PurchaseItem { Product = product2, Quantity = 10m, UnitCost = 2m });
        seed.Receipts.Add(purchase);

        await seed.SaveChangesAsync();
        return purchase.Id;
    }

    [Fact]
    public async Task ListAsync_leaves_the_change_tracker_empty()
    {
        await SeedPurchaseWithLinesSupplierAndDocumentAsync();
        await using var db = TestAppDbContext.Unrestricted(_options);
        var store = new EfPurchaseStore(db, TestCostingUseCases.Rebuild(db), TestClocks.Fixed);

        var purchases = await store.ListAsync(null, CancellationToken.None);

        Assert.NotEmpty(purchases);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task FindByIdAsync_leaves_the_change_tracker_empty()
    {
        var id = await SeedPurchaseWithLinesSupplierAndDocumentAsync();
        await using var db = TestAppDbContext.Unrestricted(_options);
        var store = new EfPurchaseStore(db, TestCostingUseCases.Rebuild(db), TestClocks.Fixed);

        var purchase = await store.FindByIdAsync(id, CancellationToken.None);

        Assert.NotNull(purchase);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    /// <summary>
    /// Issue #1289's token-propagation criterion: a token already cancelled before the call is made
    /// must reach the database, rather than the store silently substituting one of its own that can
    /// never fire.
    /// </summary>
    [Fact]
    public async Task ListAsync_observes_a_token_already_cancelled_before_the_call()
    {
        await SeedPurchaseWithLinesSupplierAndDocumentAsync();
        await using var db = TestAppDbContext.Unrestricted(_options);
        var store = new EfPurchaseStore(db, TestCostingUseCases.Rebuild(db), TestClocks.Fixed);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.ListAsync(null, new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task FindByIdAsync_observes_a_token_already_cancelled_before_the_call()
    {
        var id = await SeedPurchaseWithLinesSupplierAndDocumentAsync();
        await using var db = TestAppDbContext.Unrestricted(_options);
        var store = new EfPurchaseStore(db, TestCostingUseCases.Rebuild(db), TestClocks.Fixed);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.FindByIdAsync(id, new CancellationToken(canceled: true)));
    }

    /// <summary>
    /// The byte-identical-response acceptance criterion's persistence half: the no-tracking,
    /// split-query read maps a purchase with several lines, a supplier and a document to exactly the
    /// same field values the tracking read did, for every field the response serialises.
    /// </summary>
    [Fact]
    public async Task ListAsync_and_FindByIdAsync_agree_on_every_field_of_a_fully_populated_purchase()
    {
        var id = await SeedPurchaseWithLinesSupplierAndDocumentAsync();
        await using var db = TestAppDbContext.Unrestricted(_options);
        var store = new EfPurchaseStore(db, TestCostingUseCases.Rebuild(db), TestClocks.Fixed);

        var listed = Assert.Single(await store.ListAsync(null, CancellationToken.None));
        var found = await store.FindByIdAsync(id, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(listed.Id, found!.Id);
        Assert.Equal("Weekly restock", listed.Title);
        Assert.Equal(30m, listed.TotalAmount);
        Assert.Equal("Acme", listed.Supplier?.Name);
        Assert.Equal("abc-def.jpg", listed.StoredFileName);
        Assert.Equal(2, listed.Items.Count);
        Assert.Contains(listed.Items, item => item.Product?.Name == "Coke" && item.Quantity == 24m);
        Assert.Contains(listed.Items, item => item.Product?.Name == "Chips" && item.Quantity == 10m);
    }
}
