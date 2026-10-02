using Inventory.Domain.Exceptions;
using Inventory.Domain.SupplierOrders;

namespace Inventory.Application.SupplierOrders;

/// <summary>
/// The supplier-order creation use case. Mirrors the former
/// <c>InventoryApi.Services.SupplierOrderService.Create</c> exactly: a deliberate validation
/// failure throws <see cref="DomainValidationException"/> (issue #59), which the HTTP boundary's
/// central <c>DomainExceptionHandler</c> maps to a 400, and an unknown supplier returns
/// <c>null</c> for the controller to map to its own 400 instead.
/// </summary>
public sealed class CreateSupplierOrder
{
    private readonly ISupplierOrderStore _store;

    public CreateSupplierOrder(ISupplierOrderStore store)
    {
        _store = store;
    }

    public async Task<SupplierOrderRecord?> Handle(SupplierOrderCreateFields fields, CancellationToken cancellationToken)
    {
        var lines = fields.Lines.Select(line => new SupplierOrderLineCandidate(line.ProductId, line.QuantityOrdered)).ToList();

        if (SupplierOrderLineValidationPolicy.HasInvalidQuantity(lines))
            throw new DomainValidationException(SupplierOrderLineValidationPolicy.InvalidQuantityMessage);

        if (!await _store.SupplierExistsAsync(fields.SupplierId, cancellationToken))
            return null;

        var productIds = lines.Select(line => line.ProductId).Distinct().ToList();
        if (SupplierOrderLineValidationPolicy.HasDuplicateProduct(lines) ||
            !await _store.AllProductsExistAsync(productIds, cancellationToken))
            throw new DomainValidationException(SupplierOrderLineValidationPolicy.DuplicateOrUnknownProductMessage);

        return await _store.CreateAsync(fields, cancellationToken);
    }
}
