using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

public class EfSupplierStoreTests
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;

    private static async Task<DbContextOptions<AppDbContext>> CreateSqliteAsync(SqliteConnection connection)
    {
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var setup = TestAppDbContext.Unrestricted(options);
        await setup.Database.EnsureCreatedAsync();
        return options;
    }

    [Fact]
    public async Task AddAsync_persists_supplier_and_returns_generated_id()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfSupplierStore(db);

        var created = await store.AddAsync("Acme", "Jane", "555-1234", "jane@acme.test", "1 Main St", CancellationToken.None);

        Assert.True(created.Id > 0);
        Assert.Equal("Acme", created.Name);

        await using var verify = TestAppDbContext.For(options, BusinessA);
        var persisted = Assert.Single(await verify.Suppliers.ToListAsync());
        Assert.Equal(created.Id, persisted.Id);
        Assert.Equal("Jane", persisted.ContactName);
    }

    [Fact]
    public async Task UpdateAsync_changes_fields_and_returns_updated_record()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfSupplierStore(db);
        var created = await store.AddAsync("Acme", "Jane", "555-1234", "jane@acme.test", "1 Main St", CancellationToken.None);

        var updated = await store.UpdateAsync(created.Id, "Acme Updated", "John", "555-9999", "john@acme.test", "2 Main St", CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal(created.Id, updated!.Id);
        Assert.Equal("Acme Updated", updated.Name);
        Assert.Equal("John", updated.ContactName);
    }

    [Fact]
    public async Task UpdateAsync_returns_null_for_missing_supplier()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfSupplierStore(db);

        var updated = await store.UpdateAsync(999, "name", null, null, null, null, CancellationToken.None);

        Assert.Null(updated);
    }

    [Fact]
    public async Task DeleteAsync_removes_existing_supplier_and_returns_true()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfSupplierStore(db);
        var created = await store.AddAsync("Acme", null, null, null, null, CancellationToken.None);

        var deleted = await store.DeleteAsync(created.Id, CancellationToken.None);

        Assert.True(deleted);
        Assert.Empty(await db.Suppliers.ToListAsync());
    }

    [Fact]
    public async Task DeleteAsync_returns_false_for_missing_supplier()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfSupplierStore(db);

        var deleted = await store.DeleteAsync(999, CancellationToken.None);

        Assert.False(deleted);
    }

    /// <summary>
    /// Supplier name has no uniqueness constraint today (AppDbContext indexes but does not
    /// enforce uniqueness on Supplier.Name) - this locks in that existing, deliberately
    /// permissive behavior against the real relational schema.
    /// </summary>
    [Fact]
    public async Task Two_suppliers_with_the_same_name_are_both_persisted()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfSupplierStore(db);

        await store.AddAsync("Same Name", null, null, null, null, CancellationToken.None);
        await store.AddAsync("Same Name", null, null, null, null, CancellationToken.None);

        var all = await store.ListOrderedByNameAsync(CancellationToken.None);
        Assert.Equal(2, all.Count);
    }

    /// <summary>
    /// The central tenant filter (issue #64), exercised through this adapter: a supplier owned
    /// by another business is invisible to list/find, and cannot be updated or deleted by id.
    /// </summary>
    [Fact]
    public async Task Another_business_supplier_is_invisible_to_list_find_update_and_delete()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        int otherBusinessSupplierId;
        await using (var seed = TestAppDbContext.For(options, BusinessB))
        {
            var store = new EfSupplierStore(seed);
            var created = await store.AddAsync("B Supplier", null, null, null, null, CancellationToken.None);
            otherBusinessSupplierId = created.Id;
        }

        await using var db = TestAppDbContext.For(options, BusinessA);
        var storeAsA = new EfSupplierStore(db);

        Assert.Empty(await storeAsA.ListOrderedByNameAsync(CancellationToken.None));
        Assert.Null(await storeAsA.FindByIdAsync(otherBusinessSupplierId, CancellationToken.None));
        Assert.Null(await storeAsA.UpdateAsync(otherBusinessSupplierId, "Hijacked", null, null, null, null, CancellationToken.None));
        Assert.False(await storeAsA.DeleteAsync(otherBusinessSupplierId, CancellationToken.None));

        await using var verify = TestAppDbContext.Unrestricted(options);
        var stillThere = Assert.Single(await verify.Suppliers.ToListAsync());
        Assert.Equal("B Supplier", stillThere.Name);
    }
}
