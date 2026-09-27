using Inventory.Application.Suppliers;
using Xunit;

namespace InventoryApi.Tests.Application.Suppliers;

public class SupplierUseCaseTests
{
    [Fact]
    public async Task Create_then_list_returns_supplier_ordered_by_name()
    {
        var store = new FakeSupplierStore();
        await new CreateSupplier(store).Handle("Zebra Co", null, null, null, null, CancellationToken.None);
        await new CreateSupplier(store).Handle("Apple Co", "Contact", "555", "a@b.com", "1 Street", CancellationToken.None);

        var result = await new ListSuppliers(store).Handle(CancellationToken.None);

        Assert.Equal(new[] { "Apple Co", "Zebra Co" }, result.Select(x => x.Name));
    }

    [Fact]
    public async Task Get_returns_null_for_missing_supplier()
    {
        var store = new FakeSupplierStore();

        var result = await new GetSupplier(store).Handle(999, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Get_returns_existing_supplier()
    {
        var store = new FakeSupplierStore();
        var created = await new CreateSupplier(store).Handle("s1", "cn", "p", "e", "a", CancellationToken.None);

        var result = await new GetSupplier(store).Handle(created.Id, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("s1", result!.Name);
    }

    [Fact]
    public async Task Update_existing_supplier_returns_true_and_persists_changes()
    {
        var store = new FakeSupplierStore();
        var created = await new CreateSupplier(store).Handle("s1", "cn", "p", "e", "a", CancellationToken.None);

        var updated = await new UpdateSupplier(store).Handle(created.Id, "s1-up", "cn2", "p2", "e2", "a2", CancellationToken.None);

        Assert.True(updated);
        var fetched = await new GetSupplier(store).Handle(created.Id, CancellationToken.None);
        Assert.Equal("s1-up", fetched!.Name);
    }

    [Fact]
    public async Task Update_missing_supplier_returns_false()
    {
        var store = new FakeSupplierStore();

        var updated = await new UpdateSupplier(store).Handle(999, "name", null, null, null, null, CancellationToken.None);

        Assert.False(updated);
    }

    [Fact]
    public async Task Delete_existing_supplier_returns_true_and_removes_it()
    {
        var store = new FakeSupplierStore();
        var created = await new CreateSupplier(store).Handle("s1", null, null, null, null, CancellationToken.None);

        var deleted = await new DeleteSupplier(store).Handle(created.Id, CancellationToken.None);

        Assert.True(deleted);
        Assert.Null(await new GetSupplier(store).Handle(created.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_missing_supplier_returns_false()
    {
        var store = new FakeSupplierStore();

        var deleted = await new DeleteSupplier(store).Handle(999, CancellationToken.None);

        Assert.False(deleted);
    }

    /// <summary>
    /// Supplier name has no uniqueness constraint today (AppDbContext indexes but does not
    /// enforce uniqueness on Supplier.Name) - this locks in that existing, deliberately
    /// permissive behavior so a future change cannot silently start rejecting duplicates.
    /// </summary>
    [Fact]
    public async Task Creating_a_supplier_with_a_duplicate_name_is_allowed()
    {
        var store = new FakeSupplierStore();
        await new CreateSupplier(store).Handle("Same Name", null, null, null, null, CancellationToken.None);

        var second = await new CreateSupplier(store).Handle("Same Name", null, null, null, null, CancellationToken.None);

        var all = await new ListSuppliers(store).Handle(CancellationToken.None);
        Assert.Equal(2, all.Count);
        Assert.Equal("Same Name", second.Name);
    }
}
