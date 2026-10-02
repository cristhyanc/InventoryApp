using Inventory.Application.SupplierOrders;
using Inventory.Domain.Exceptions;
using Inventory.Domain.SupplierOrders;
using Xunit;

namespace InventoryApi.Tests.Application.SupplierOrders;

public class CreateSupplierOrderTests
{
    private static SupplierOrderCreateFields Fields(params SupplierOrderLineInput[] lines) =>
        new(1, new DateTime(2026, 9, 1), null, "Order #1", null, lines);

    [Fact]
    public async Task An_order_with_no_lines_throws_a_domain_validation_exception_without_checking_the_supplier()
    {
        var store = new FakeSupplierOrderStore { SupplierExists = false };
        var useCase = new CreateSupplierOrder(store);

        var exception = await Assert.ThrowsAsync<DomainValidationException>(() =>
            useCase.Handle(Fields(), CancellationToken.None));

        Assert.Equal(SupplierOrderLineValidationPolicy.InvalidQuantityMessage, exception.Message);
    }

    [Fact]
    public async Task A_non_positive_quantity_throws_a_domain_validation_exception()
    {
        var store = new FakeSupplierOrderStore();
        var useCase = new CreateSupplierOrder(store);

        var exception = await Assert.ThrowsAsync<DomainValidationException>(() =>
            useCase.Handle(Fields(new SupplierOrderLineInput(1, 0, null, null)), CancellationToken.None));

        Assert.Equal(SupplierOrderLineValidationPolicy.InvalidQuantityMessage, exception.Message);
    }

    [Fact]
    public async Task An_unknown_supplier_returns_null_before_checking_products()
    {
        var store = new FakeSupplierOrderStore { SupplierExists = false, AllProductsExist = false };
        var useCase = new CreateSupplierOrder(store);

        var result = await useCase.Handle(Fields(new SupplierOrderLineInput(1, 5, null, null)), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task A_duplicate_product_line_throws_a_domain_validation_exception()
    {
        var store = new FakeSupplierOrderStore();
        var useCase = new CreateSupplierOrder(store);

        var exception = await Assert.ThrowsAsync<DomainValidationException>(() => useCase.Handle(
            Fields(new SupplierOrderLineInput(1, 5, null, null), new SupplierOrderLineInput(1, 3, null, null)),
            CancellationToken.None));

        Assert.Equal(SupplierOrderLineValidationPolicy.DuplicateOrUnknownProductMessage, exception.Message);
    }

    [Fact]
    public async Task An_unknown_product_throws_a_domain_validation_exception()
    {
        var store = new FakeSupplierOrderStore { AllProductsExist = false };
        var useCase = new CreateSupplierOrder(store);

        var exception = await Assert.ThrowsAsync<DomainValidationException>(() =>
            useCase.Handle(Fields(new SupplierOrderLineInput(404, 5, null, null)), CancellationToken.None));

        Assert.Equal(SupplierOrderLineValidationPolicy.DuplicateOrUnknownProductMessage, exception.Message);
    }

    [Fact]
    public async Task A_valid_order_is_persisted()
    {
        var store = new FakeSupplierOrderStore();
        var useCase = new CreateSupplierOrder(store);

        var result = await useCase.Handle(Fields(new SupplierOrderLineInput(1, 24, 0.5m, null)), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(SupplierOrderStatus.Ordered, result!.Status);
        Assert.Equal(store.LastCreated, result);
    }
}
