using Inventory.Domain.Products;

namespace Inventory.Application.Products;

/// <summary>
/// The product update use case: an unknown id answers not-found even when the request also carries
/// invalid restock settings, matching the former <c>InventoryApi.Services.ProductService.Update</c>
/// exactly (issue #240).
///
/// The <see cref="IProductStore.ExistsAsync"/> read is only a fast pre-check that preserves that
/// ordering; the authoritative not-found answer is <see cref="IProductStore.UpdateAsync"/>'s own
/// outcome, so a product deleted by another request between the read and the write reports
/// not-found rather than a success that changed nothing.
/// </summary>
public sealed class UpdateProduct
{
    private readonly IProductStore _store;

    public UpdateProduct(IProductStore store)
    {
        _store = store;
    }

    public async Task<UpdateProductResult> Handle(long id, ProductUpdateFields fields, CancellationToken cancellationToken)
    {
        if (!await _store.ExistsAsync(id, cancellationToken))
            return UpdateProductResult.NotFound();

        var error = ProductRestockPolicy.Validate(fields.LowStockThreshold, fields.RestockTo);
        if (error is not null) return UpdateProductResult.Invalid(error);

        return await _store.UpdateAsync(id, fields, cancellationToken)
            ? UpdateProductResult.Success()
            : UpdateProductResult.NotFound();
    }
}
