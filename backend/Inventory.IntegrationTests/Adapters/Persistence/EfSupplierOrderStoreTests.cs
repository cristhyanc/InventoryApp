using Inventory.Application.SupplierOrders;
using DomainStatus = Inventory.Domain.SupplierOrders.SupplierOrderStatus;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational SQLite tests for <see cref="EfSupplierOrderStore"/>: the supplier/product joins
/// <see cref="EfSupplierOrderStore.CreateAsync"/>/<see cref="EfSupplierOrderStore.FindByIdAsync"/>
/// resolve, and the status-transition guard <see cref="EfSupplierOrderStore.CancelAsync"/> enforces
/// (issue #281).
/// </summary>
public sealed class EfSupplierOrderStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public EfSupplierOrderStoreTests()
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

    [Fact]
    public async Task CreateAsync_persists_lines_and_resolves_the_supplier_and_product_joins()
    {
        await using (var seed = TestAppDbContext.Unrestricted(_options))
        {
            seed.Suppliers.Add(new Supplier { Id = 1, Name = "Costco" });
            seed.Products.Add(new Product { Id = 1, Name = "Coke" });
            await seed.SaveChangesAsync();
        }

        int orderId;
        await using (var db = TestAppDbContext.Unrestricted(_options))
        {
            var store = new EfSupplierOrderStore(db);
            var created = await store.CreateAsync(
                new SupplierOrderCreateFields(1, new DateTime(2026, 9, 1), null, "Order #1", null,
                    [new SupplierOrderLineInput(1, 24, 0.5m, null)]),
                CancellationToken.None);
            orderId = created.Id;
            Assert.Equal("Costco", created.Supplier!.Name);
            Assert.Equal("Coke", created.Lines.Single().Product.Name);
        }

        await using var verify = TestAppDbContext.Unrestricted(_options);
        var store2 = new EfSupplierOrderStore(verify);
        var fetched = await store2.FindByIdAsync(orderId, CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.Equal(DomainStatus.Ordered, fetched!.Status);
        Assert.Equal(24m, fetched.Lines.Single().QuantityOrdered);
    }

    [Fact]
    public async Task CancelAsync_returns_false_and_leaves_a_received_order_untouched()
    {
        int orderId;
        await using (var seed = TestAppDbContext.Unrestricted(_options))
        {
            seed.Products.Add(new Product { Id = 1, Name = "Coke" });
            var order = new SupplierOrder
            {
                Status = SupplierOrderStatus.Received,
                Lines = { new SupplierOrderLine { ProductId = 1, QuantityOrdered = 5, QuantityReceived = 5 } },
            };
            seed.SupplierOrders.Add(order);
            await seed.SaveChangesAsync();
            orderId = order.Id;
        }

        await using var db = TestAppDbContext.Unrestricted(_options);
        var store = new EfSupplierOrderStore(db);

        Assert.False(await store.CancelAsync(orderId, CancellationToken.None));
        Assert.Equal(SupplierOrderStatus.Received, (await db.SupplierOrders.FindAsync(orderId))!.Status);
    }

    [Fact]
    public async Task ListActiveAsync_excludes_cancelled_and_received_orders()
    {
        await using (var seed = TestAppDbContext.Unrestricted(_options))
        {
            seed.Products.Add(new Product { Id = 1, Name = "Coke" });
            seed.SupplierOrders.AddRange(
                new SupplierOrder { Status = SupplierOrderStatus.Ordered, Lines = { new SupplierOrderLine { ProductId = 1, QuantityOrdered = 5 } } },
                new SupplierOrder { Status = SupplierOrderStatus.Cancelled, Lines = { new SupplierOrderLine { ProductId = 1, QuantityOrdered = 5 } } },
                new SupplierOrder { Status = SupplierOrderStatus.Received, Lines = { new SupplierOrderLine { ProductId = 1, QuantityOrdered = 5, QuantityReceived = 5 } } });
            await seed.SaveChangesAsync();
        }

        await using var db = TestAppDbContext.Unrestricted(_options);
        var store = new EfSupplierOrderStore(db);

        var active = await store.ListActiveAsync(CancellationToken.None);

        Assert.Equal(DomainStatus.Ordered, Assert.Single(active).Status);
    }
}
