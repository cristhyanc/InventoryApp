using Inventory.Application.SupplierOrders;
using Inventory.Domain.Exceptions;
using InventoryApi.Adapters.Mapping;
using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
using InventoryApi.DTOs;
using InventoryApi.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Services;

/// <summary>
/// The former <c>SupplierOrderServiceTests</c>, retargeted onto the
/// <see cref="Inventory.Application.SupplierOrders"/> use cases over the real EF adapter when issue
/// #304 deleted the <c>SupplierOrderService</c> delegator the supplier-order endpoints used to
/// call. The behaviour covered is unchanged - the active-order selection, the eagerly loaded
/// supplier/product/line detail, the create validation and its <see cref="DomainValidationException"/>
/// messages, and the cancel rules - and the outstanding-quantity cases now assert the API-owned
/// <c>SupplierOrderLineResponse</c> clients actually receive instead of the entity.
/// </summary>
public class SupplierOrderUseCaseTests
{
    private static AppDbContext CreateDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return TestAppDbContext.Unrestricted(options);
    }

    private static SupplierOrderUseCases CreateUseCases(AppDbContext db)
    {
        var store = new EfSupplierOrderStore(db);
        return new SupplierOrderUseCases(
            new ListActiveSupplierOrders(store),
            new GetSupplierOrder(store),
            new CreateSupplierOrder(store),
            new CancelSupplierOrder(store));
    }

    /// <summary>
    /// Creates an order exactly as <c>SupplierOrdersController.Create</c> does, mapping the posted
    /// DTO onto the Application layer's own input type.
    /// </summary>
    private static Task<SupplierOrderRecord?> Create(SupplierOrderUseCases useCases, SupplierOrderCreateDto dto) =>
        useCases.Create.Handle(
            new SupplierOrderCreateFields(
                dto.SupplierId,
                dto.OrderDate,
                dto.ExpectedDate,
                dto.Reference,
                dto.Notes,
                dto.Lines.Select(line => new SupplierOrderLineInput(line.ProductId, line.QuantityOrdered, line.UnitPrice, line.Notes)).ToList()),
            CancellationToken.None);

    /// <summary>
    /// The response a client receives for one order, through the same mapper the controller uses.
    /// </summary>
    private static async Task<SupplierOrderResponse?> GetByIdResponse(SupplierOrderUseCases useCases, int id)
    {
        var record = await useCases.Get.Handle(id, CancellationToken.None);
        return record is null ? null : SupplierOrderResponseMapper.ToResponse(record);
    }

    [Fact]
    public async Task GetById_ReturnsOrderWithSupplierProductsAndLines()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Costco" });
        db.Products.Add(new Product { Id = 1, Name = "Coke" });
        db.Products.Add(new Product { Id = 2, Name = "Caramello" });
        db.SupplierOrders.Add(new SupplierOrder
        {
            SupplierId = 1,
            OrderDate = new DateTime(2026, 9, 1),
            ExpectedDate = new DateTime(2026, 9, 10),
            Reference = "Order #42",
            Lines = new[]
            {
                new SupplierOrderLine { ProductId = 1, QuantityOrdered = 24, UnitPrice = 0.50m },
                new SupplierOrderLine { ProductId = 2, QuantityOrdered = 12, UnitPrice = 1.00m }
            }
        });
        await db.SaveChangesAsync();

        var order = await GetByIdResponse(CreateUseCases(db), 1);

        Assert.NotNull(order);
        Assert.Equal(1, order.Id);
        Assert.NotNull(order.Supplier);
        Assert.Equal("Costco", order.Supplier.Name);
        Assert.Equal(2, order.Lines.Count);
        var lines = order.Lines.ToList();
        Assert.Equal("Coke", lines[0].Product.Name);
        Assert.Equal(24m, lines[0].QuantityOrdered);
        Assert.Equal(0.50m, lines[0].UnitPrice);
        Assert.Equal("Caramello", lines[1].Product.Name);
        Assert.Equal(12m, lines[1].QuantityOrdered);
        Assert.Equal(1.00m, lines[1].UnitPrice);
    }

    [Fact]
    public async Task GetById_ReturnsNullForUnknownId()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());

        var order = await GetByIdResponse(CreateUseCases(db), 999);

        Assert.Null(order);
    }

    [Fact]
    public async Task GetById_CalculatesOutstandingQuantityCorrectly()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Costco" });
        db.Products.Add(new Product { Id = 1, Name = "Coke" });
        db.SupplierOrders.Add(new SupplierOrder
        {
            SupplierId = 1,
            Lines = new[]
            {
                new SupplierOrderLine { ProductId = 1, QuantityOrdered = 24, QuantityReceived = 6 }
            }
        });
        await db.SaveChangesAsync();

        var order = await GetByIdResponse(CreateUseCases(db), 1);

        Assert.NotNull(order);
        var lines = order.Lines.ToList();
        Assert.Equal(24m, lines[0].QuantityOrdered);
        Assert.Equal(6m, lines[0].QuantityReceived);
        Assert.Equal(18m, lines[0].OutstandingQuantity);
    }

    [Fact]
    public async Task GetById_OutstandingQuantityIsZeroForFullyReceivedOrder()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Costco" });
        db.Products.Add(new Product { Id = 1, Name = "Coke" });
        db.SupplierOrders.Add(new SupplierOrder
        {
            SupplierId = 1,
            Status = SupplierOrderStatus.Received,
            Lines = new[]
            {
                new SupplierOrderLine { ProductId = 1, QuantityOrdered = 24, QuantityReceived = 24 }
            }
        });
        await db.SaveChangesAsync();

        var order = await GetByIdResponse(CreateUseCases(db), 1);

        Assert.NotNull(order);
        var lines = order.Lines.ToList();
        Assert.Equal(0m, lines[0].OutstandingQuantity);
    }

    [Fact]
    public async Task GetById_OutstandingQuantityIsZeroForCancelledOrder()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Costco" });
        db.Products.Add(new Product { Id = 1, Name = "Coke" });
        db.SupplierOrders.Add(new SupplierOrder
        {
            SupplierId = 1,
            Status = SupplierOrderStatus.Cancelled,
            Lines = new[]
            {
                new SupplierOrderLine { ProductId = 1, QuantityOrdered = 24, QuantityReceived = 0 }
            }
        });
        await db.SaveChangesAsync();

        var order = await GetByIdResponse(CreateUseCases(db), 1);

        Assert.NotNull(order);
        var lines = order.Lines.ToList();
        Assert.Equal(0m, lines[0].OutstandingQuantity);
    }

    [Fact]
    public async Task GetActive_ReturnsOnlyNonCancelledAndNonReceivedOrders()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Costco" });
        db.Products.Add(new Product { Id = 1, Name = "Coke" });
        db.SupplierOrders.AddRange(
            new SupplierOrder { Id = 1, SupplierId = 1, Status = SupplierOrderStatus.Ordered, Lines = { new SupplierOrderLine { ProductId = 1, QuantityOrdered = 10 } } },
            new SupplierOrder { Id = 2, SupplierId = 1, Status = SupplierOrderStatus.PartiallyReceived, Lines = { new SupplierOrderLine { ProductId = 1, QuantityOrdered = 10 } } },
            new SupplierOrder { Id = 3, SupplierId = 1, Status = SupplierOrderStatus.Received, Lines = { new SupplierOrderLine { ProductId = 1, QuantityOrdered = 10 } } },
            new SupplierOrder { Id = 4, SupplierId = 1, Status = SupplierOrderStatus.Cancelled, Lines = { new SupplierOrderLine { ProductId = 1, QuantityOrdered = 10 } } }
        );
        await db.SaveChangesAsync();

        var orders = await CreateUseCases(db).ListActive.Handle(CancellationToken.None);

        Assert.Equal(2, orders.Count);
        Assert.True(orders.All(o => o.Id == 1 || o.Id == 2));
    }

    [Fact]
    public async Task Create_CreatesOrderWithLines()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Costco" });
        db.Products.Add(new Product { Id = 1, Name = "Coke" });
        db.Products.Add(new Product { Id = 2, Name = "Caramello" });
        await db.SaveChangesAsync();

        var dto = new SupplierOrderCreateDto(
            1,
            new DateTime(2026, 9, 1),
            new DateTime(2026, 9, 10),
            "Order #42",
            "Priority",
            new[]
            {
                new SupplierOrderLineCreateDto(1, 24, 0.50m),
                new SupplierOrderLineCreateDto(2, 12, 1.00m)
            }
        );
        var order = await Create(CreateUseCases(db), dto);

        Assert.NotNull(order);
        Assert.Equal(1, order.SupplierId);
        Assert.Equal("Order #42", order.Reference);
        Assert.Equal("Priority", order.Notes);
        Assert.Equal(2, order.Lines.Count);
        Assert.Equal(Inventory.Domain.SupplierOrders.SupplierOrderStatus.Ordered, order.Status);
    }

    [Fact]
    public async Task Create_ReturnsNullForUnknownSupplier()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Products.Add(new Product { Id = 1, Name = "Coke" });
        await db.SaveChangesAsync();

        var order = await Create(CreateUseCases(db), new SupplierOrderCreateDto(
            404, new DateTime(2026, 9, 1), null, null, null, [new SupplierOrderLineCreateDto(1, 5)]));

        Assert.Null(order);
    }

    // Issue #59: these two checks are deliberate, caller-facing validation, so they throw the
    // narrowly typed DomainValidationException the central handler is allowed to publish as a 400.
    // An ordinary InvalidOperationException here would now become a generic logged 500 instead.
    [Fact]
    public async Task Create_rejects_a_non_positive_quantity_with_a_domain_validation_exception()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Costco" });
        db.Products.Add(new Product { Id = 1, Name = "Coke" });
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<DomainValidationException>(() =>
            Create(CreateUseCases(db), new SupplierOrderCreateDto(
                1, new DateTime(2026, 9, 1), null, null, null, [new SupplierOrderLineCreateDto(1, 0)])));

        Assert.Equal("An order must include at least one positive whole-unit quantity.", exception.Message);
    }

    /// <summary>
    /// A fractional quantity is rejected by the same whole-unit rule. This case moved here from the
    /// former <c>ProductServiceTests</c> when issue #303 retargeted that file onto the product use
    /// cases; the behaviour it covers is unchanged.
    /// </summary>
    [Fact]
    public async Task Create_rejects_a_fractional_quantity_with_a_domain_validation_exception()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Costco" });
        db.Products.Add(new Product { Id = 1, Name = "Coke" });
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<DomainValidationException>(() =>
            Create(CreateUseCases(db), new SupplierOrderCreateDto(
                1, new DateTime(2026, 9, 1), null, null, null, [new SupplierOrderLineCreateDto(1, 1.5m)])));

        Assert.Equal("An order must include at least one positive whole-unit quantity.", exception.Message);
    }

    [Fact]
    public async Task Create_rejects_an_unknown_product_with_a_domain_validation_exception()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Costco" });
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<DomainValidationException>(() =>
            Create(CreateUseCases(db), new SupplierOrderCreateDto(
                1, new DateTime(2026, 9, 1), null, null, null, [new SupplierOrderLineCreateDto(404, 5)])));

        Assert.Equal("Each order line must reference a distinct existing product.", exception.Message);
    }

    [Fact]
    public async Task Cancel_CancelsOrderSuccessfully()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Costco" });
        db.Products.Add(new Product { Id = 1, Name = "Coke" });
        db.SupplierOrders.Add(new SupplierOrder
        {
            SupplierId = 1,
            Status = SupplierOrderStatus.Ordered,
            Lines = { new SupplierOrderLine { ProductId = 1, QuantityOrdered = 10 } }
        });
        await db.SaveChangesAsync();

        var result = await CreateUseCases(db).Cancel.Handle(1, CancellationToken.None);

        Assert.True(result);
        var order = await db.SupplierOrders.FindAsync(1);
        Assert.Equal(SupplierOrderStatus.Cancelled, order!.Status);
    }

    [Fact]
    public async Task Cancel_ReturnsFalseForUnknownId()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());

        var result = await CreateUseCases(db).Cancel.Handle(999, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task Cancel_ReturnsFalseForAlreadyCancelledOrder()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Costco" });
        db.Products.Add(new Product { Id = 1, Name = "Coke" });
        db.SupplierOrders.Add(new SupplierOrder
        {
            SupplierId = 1,
            Status = SupplierOrderStatus.Cancelled,
            Lines = { new SupplierOrderLine { ProductId = 1, QuantityOrdered = 10 } }
        });
        await db.SaveChangesAsync();

        var result = await CreateUseCases(db).Cancel.Handle(1, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task Cancel_ReturnsFalseForReceivedOrder()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Costco" });
        db.Products.Add(new Product { Id = 1, Name = "Coke" });
        db.SupplierOrders.Add(new SupplierOrder
        {
            SupplierId = 1,
            Status = SupplierOrderStatus.Received,
            Lines = { new SupplierOrderLine { ProductId = 1, QuantityOrdered = 10, QuantityReceived = 10 } }
        });
        await db.SaveChangesAsync();

        var result = await CreateUseCases(db).Cancel.Handle(1, CancellationToken.None);

        Assert.False(result);
    }

    /// <summary>
    /// The supplier-order use cases <c>SupplierOrdersController</c> is composed from, bundled only
    /// so these tests can build them in one step. It holds no behaviour of its own.
    /// </summary>
    private sealed record SupplierOrderUseCases(
        ListActiveSupplierOrders ListActive,
        GetSupplierOrder Get,
        CreateSupplierOrder Create,
        CancelSupplierOrder Cancel);
}
