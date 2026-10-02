using Inventory.Application.Products;
using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
using InventoryApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational SQLite regression tests for <see cref="EfProductStore"/>'s update outcome: the
/// existence check <see cref="UpdateProduct"/> performs first is only a read, so the store's own
/// write outcome has to be the authoritative not-found answer. These are relational rather than
/// InMemory tests because the behaviour under test is what the real provider does when the row the
/// write targets is no longer there.
/// </summary>
public class EfProductStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public EfProductStoreTests()
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

    private static ProductUpdateFields UpdateFields() => new("SKU-UPDATED", null, 5, 10, "unit", null, true);

    private async Task<long> SeedProductAsync()
    {
        await using var seed = TestAppDbContext.Unrestricted(_options);
        var product = new Product { Name = "Coke", Sku = "ORIGINAL", LowStockThreshold = 1, RestockTo = 2 };
        seed.Products.Add(product);
        await seed.SaveChangesAsync();
        return product.Id;
    }

    [Fact]
    public async Task UpdateAsync_ReturnsTrue_WhenTheProductWasUpdated()
    {
        var productId = await SeedProductAsync();

        await using var db = TestAppDbContext.Unrestricted(_options);
        var store = new EfProductStore(db, TestCostingUseCases.Rebuild(db));

        Assert.True(await store.UpdateAsync(productId, UpdateFields(), CancellationToken.None));
    }

    [Fact]
    public async Task UpdateAsync_ReturnsFalse_WhenNoSuchProductExists()
    {
        await using var db = TestAppDbContext.Unrestricted(_options);
        var store = new EfProductStore(db, TestCostingUseCases.Rebuild(db));

        Assert.False(await store.UpdateAsync(4242, UpdateFields(), CancellationToken.None));
    }

    /// <summary>
    /// The regression this guards: another request deletes the product in the window between
    /// <see cref="IProductStore.ExistsAsync"/> and <see cref="IProductStore.UpdateAsync"/>. The delete
    /// is injected deterministically at exactly that point through a decorator, over a real SQLite
    /// connection and a separate <c>AppDbContext</c>, so the update genuinely finds no row. The use
    /// case must answer not-found - the API's 404 - never success, which the API answers as 204.
    /// </summary>
    [Fact]
    public async Task UpdateProduct_ReportsNotFound_WhenTheProductIsDeletedBetweenTheExistenceCheckAndTheUpdate()
    {
        var productId = await SeedProductAsync();

        await using var db = TestAppDbContext.Unrestricted(_options);
        var store = new DeleteAfterExistenceCheckProductStore(
            new EfProductStore(db, TestCostingUseCases.Rebuild(db)),
            async () =>
            {
                await using var concurrent = TestAppDbContext.Unrestricted(_options);
                var product = await concurrent.Products.SingleAsync(candidate => candidate.Id == productId);
                concurrent.Products.Remove(product);
                await concurrent.SaveChangesAsync();
            });

        var result = await new UpdateProduct(store).Handle(productId, UpdateFields(), CancellationToken.None);

        Assert.Equal(UpdateProductOutcome.NotFound, result.Outcome);

        await using var verify = TestAppDbContext.Unrestricted(_options);
        Assert.False(await verify.Products.AnyAsync(candidate => candidate.Id == productId));
    }

    /// <summary>
    /// Runs the supplied callback immediately after the real store answers the existence check, which
    /// is the only seam where a concurrent change can be made to land inside the window under test
    /// without a race.
    /// </summary>
    private sealed class DeleteAfterExistenceCheckProductStore : IProductStore
    {
        private readonly IProductStore _inner;
        private readonly Func<Task> _onExistenceChecked;

        public DeleteAfterExistenceCheckProductStore(IProductStore inner, Func<Task> onExistenceChecked)
        {
            _inner = inner;
            _onExistenceChecked = onExistenceChecked;
        }

        public Task<long> CreateAsync(ProductCreateFields fields, CancellationToken cancellationToken) =>
            _inner.CreateAsync(fields, cancellationToken);

        public async Task<bool> ExistsAsync(long id, CancellationToken cancellationToken)
        {
            var exists = await _inner.ExistsAsync(id, cancellationToken);
            await _onExistenceChecked();
            return exists;
        }

        public Task<bool> UpdateAsync(long id, ProductUpdateFields fields, CancellationToken cancellationToken) =>
            _inner.UpdateAsync(id, fields, cancellationToken);

        public Task<bool> DeleteAsync(long id, CancellationToken cancellationToken) =>
            _inner.DeleteAsync(id, cancellationToken);
    }
}
