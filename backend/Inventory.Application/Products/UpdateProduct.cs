using Inventory.Domain.Products;

namespace Inventory.Application.Products;

/// <summary>
/// The product update use case: an unknown id answers not-found even when the request also carries
/// invalid restock settings, matching the former <c>InventoryApi.Services.ProductService.Update</c>
/// exactly (issue #240).
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

        await _store.UpdateAsync(id, fields, cancellationToken);
        return UpdateProductResult.Success();
    }
}
