using Inventory.Application.Expenses;
using Inventory.Domain.Expenses;
using InventoryApi.Adapters.Persistence;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

public class EfOperatingExpenseStoreTests
{
    private static async Task<(SqliteConnection Connection, DbContextOptions<AppDbContext> Options)> CreateSqliteAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var setup = TestAppDbContext.Unrestricted(options);
        await setup.Database.EnsureCreatedAsync();
        return (connection, options);
    }

    private static OperatingExpenseFields Fields(
        ExpenseCategory category = ExpenseCategory.Insurance,
        DateTime? expenseDate = null,
        int? supplierId = null) => new(
        expenseDate ?? new DateTime(2026, 3, 1), category, "Insurance", 10m, 1m, 11m,
        supplierId, null, null, null, null, null);

    [Fact]
    public async Task AddAsync_persists_fields_without_loading_supplier()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Unrestricted(options);
            var store = new EfOperatingExpenseStore(db);

            var record = await store.AddAsync(Fields(), null, CancellationToken.None);

            Assert.Equal("Insurance", record.Description);
            Assert.Null(record.Supplier);
            var persisted = Assert.Single(await db.OperatingExpenses.AsNoTracking().ToListAsync());
            Assert.Equal(record.Id, persisted.Id);
        }
    }

    [Fact]
    public async Task AddAsync_with_attachment_metadata_persists_it()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Unrestricted(options);
            var store = new EfOperatingExpenseStore(db);
            var attachment = new OperatingExpenseAttachmentMetadata("invoice.pdf", "abc.pdf", "application/pdf", 3);

            var record = await store.AddAsync(Fields(), attachment, CancellationToken.None);

            Assert.Equal("invoice.pdf", record.AttachmentFileName);
            Assert.Equal("abc.pdf", record.AttachmentStoredFileName);
            Assert.Equal("application/pdf", record.AttachmentContentType);
            Assert.Equal(3, record.AttachmentFileSizeBytes);
        }
    }

    [Fact]
    public async Task FindByIdAsync_returns_null_when_missing()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Unrestricted(options);
            var store = new EfOperatingExpenseStore(db);

            Assert.Null(await store.FindByIdAsync(999, CancellationToken.None));
        }
    }

    [Fact]
    public async Task FindByIdAsync_includes_the_supplier()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Unrestricted(options);
            db.Suppliers.Add(new Supplier { Id = 1, Name = "Acme" });
            await db.SaveChangesAsync();
            var store = new EfOperatingExpenseStore(db);
            var created = await store.AddAsync(Fields(supplierId: 1), null, CancellationToken.None);

            var found = await store.FindByIdAsync(created.Id, CancellationToken.None);

            Assert.NotNull(found!.Supplier);
            Assert.Equal("Acme", found.Supplier!.Name);
        }
    }

    [Fact]
    public async Task UpdateAsync_returns_null_when_missing()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Unrestricted(options);
            var store = new EfOperatingExpenseStore(db);

            Assert.Null(await store.UpdateAsync(999, Fields(), null, CancellationToken.None));
        }
    }

    [Fact]
    public async Task UpdateAsync_without_new_attachment_leaves_the_existing_attachment_untouched()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Unrestricted(options);
            var store = new EfOperatingExpenseStore(db);
            var attachment = new OperatingExpenseAttachmentMetadata("invoice.pdf", "abc.pdf", "application/pdf", 3);
            var created = await store.AddAsync(Fields(), attachment, CancellationToken.None);

            var updated = await store.UpdateAsync(created.Id, Fields(category: ExpenseCategory.Software), null, CancellationToken.None);

            Assert.NotNull(updated);
            Assert.Equal(ExpenseCategory.Software, updated!.Category);
            Assert.Equal("abc.pdf", updated.AttachmentStoredFileName);
        }
    }

    [Fact]
    public async Task UpdateAsync_with_a_new_attachment_replaces_the_metadata()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Unrestricted(options);
            var store = new EfOperatingExpenseStore(db);
            var created = await store.AddAsync(Fields(), new OperatingExpenseAttachmentMetadata("old.pdf", "old.pdf", "application/pdf", 3), CancellationToken.None);

            var updated = await store.UpdateAsync(
                created.Id, Fields(), new OperatingExpenseAttachmentMetadata("new.png", "new.png", "image/png", 5), CancellationToken.None);

            Assert.Equal("new.png", updated!.AttachmentFileName);
            Assert.Equal("new.png", updated.AttachmentStoredFileName);
            Assert.Equal("image/png", updated.AttachmentContentType);
            Assert.Equal(5, updated.AttachmentFileSizeBytes);
        }
    }

    /// <summary>
    /// Issue #52: the supplier on an updated expense must come from an explicit reload against the
    /// final SupplierId, not from EF's change tracker or lazy loading.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_reloads_the_supplier_against_the_final_value()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var seed = TestAppDbContext.Unrestricted(options);
            seed.Suppliers.Add(new Supplier { Id = 1, Name = "Acme" });
            var store = new EfOperatingExpenseStore(seed);
            var created = await store.AddAsync(Fields(), null, CancellationToken.None);

            await using var db = TestAppDbContext.Unrestricted(options);
            var updateStore = new EfOperatingExpenseStore(db);
            var updated = await updateStore.UpdateAsync(created.Id, Fields(supplierId: 1), null, CancellationToken.None);

            Assert.NotNull(updated!.Supplier);
            Assert.Equal("Acme", updated.Supplier!.Name);
        }
    }

    [Fact]
    public async Task DeleteAsync_returns_null_when_missing()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Unrestricted(options);
            var store = new EfOperatingExpenseStore(db);

            Assert.Null(await store.DeleteAsync(999, CancellationToken.None));
        }
    }

    [Fact]
    public async Task DeleteAsync_removes_the_row_and_returns_its_last_state()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Unrestricted(options);
            var store = new EfOperatingExpenseStore(db);
            var attachment = new OperatingExpenseAttachmentMetadata("invoice.pdf", "abc.pdf", "application/pdf", 3);
            var created = await store.AddAsync(Fields(), attachment, CancellationToken.None);

            var deleted = await store.DeleteAsync(created.Id, CancellationToken.None);

            Assert.NotNull(deleted);
            Assert.Equal("abc.pdf", deleted!.AttachmentStoredFileName);
            Assert.Empty(await db.OperatingExpenses.AsNoTracking().ToListAsync());
        }
    }

    [Fact]
    public async Task ListAsync_filters_by_category_and_orders_newest_first()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Unrestricted(options);
            var store = new EfOperatingExpenseStore(db);
            await store.AddAsync(Fields(ExpenseCategory.Insurance, new DateTime(2026, 1, 1)), null, CancellationToken.None);
            await store.AddAsync(Fields(ExpenseCategory.Software, new DateTime(2026, 2, 1)), null, CancellationToken.None);
            await store.AddAsync(Fields(ExpenseCategory.Insurance, new DateTime(2026, 3, 1)), null, CancellationToken.None);

            var filter = new OperatingExpenseFilter(null, null, ExpenseCategory.Insurance, null, null, null);
            var result = await store.ListAsync(filter, CancellationToken.None);

            Assert.Equal(
                new[] { new DateTime(2026, 3, 1), new DateTime(2026, 1, 1) },
                result.Select(x => x.ExpenseDate));
        }
    }

    [Fact]
    public async Task ListAsync_filters_by_date_range_using_an_exclusive_upper_bound()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Unrestricted(options);
            var store = new EfOperatingExpenseStore(db);
            await store.AddAsync(Fields(expenseDate: new DateTime(2026, 2, 28)), null, CancellationToken.None);
            await store.AddAsync(Fields(expenseDate: new DateTime(2026, 3, 1)), null, CancellationToken.None);
            await store.AddAsync(Fields(expenseDate: new DateTime(2026, 3, 31)), null, CancellationToken.None);
            await store.AddAsync(Fields(expenseDate: new DateTime(2026, 4, 1)), null, CancellationToken.None);

            var filter = new OperatingExpenseFilter(new DateTime(2026, 3, 1), new DateTime(2026, 3, 31), null, null, null, null);
            var result = await store.ListAsync(filter, CancellationToken.None);

            Assert.Equal(
                new[] { new DateTime(2026, 3, 31), new DateTime(2026, 3, 1) },
                result.Select(x => x.ExpenseDate));
        }
    }

    [Fact]
    public async Task ListAsync_projects_the_suppliers_name_without_the_full_supplier()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Unrestricted(options);
            db.Suppliers.Add(new Supplier { Id = 1, Name = "Acme" });
            await db.SaveChangesAsync();
            var store = new EfOperatingExpenseStore(db);
            await store.AddAsync(Fields(supplierId: 1), null, CancellationToken.None);

            var result = await store.ListAsync(new OperatingExpenseFilter(null, null, null, null, null, null), CancellationToken.None);

            Assert.Equal("Acme", Assert.Single(result).SupplierName);
        }
    }
}
