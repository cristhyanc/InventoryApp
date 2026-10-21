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
///
/// <para>Since issue #736 the submitted fields also carry the product's name and category - and,
/// until issue #792 removed the field, its selling price. The deterministic invariant over them
/// lives in <see cref="ProductCataloguePolicy"/> and is checked here, before anything is written; the two
/// refusals that depend on the stored row - a provider-managed name or category being changed, and a
/// category this business does not own - are decided by the store, inside the same operation as the
/// write (see <see cref="IProductStore.UpdateAsync"/>). Both reach the caller as this use case's
/// <see cref="UpdateProductOutcome.Invalid"/> outcome with the refusal's own message.</para>
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
        ArgumentNullException.ThrowIfNull(fields);

        if (!await _store.ExistsAsync(id, cancellationToken))
            return UpdateProductResult.NotFound();

        var error = ProductRestockPolicy.Validate(fields.LowStockThreshold, fields.RestockTo)
            ?? ProductCataloguePolicy.Validate(fields.Name);
        if (error is not null) return UpdateProductResult.Invalid(error);

        var attempt = await _store.UpdateAsync(id, fields, cancellationToken);
        return attempt.Outcome switch
        {
            ProductWriteOutcome.Succeeded => UpdateProductResult.Success(),
            ProductWriteOutcome.Refused => UpdateProductResult.Invalid(attempt.Refusal!),
            _ => UpdateProductResult.NotFound(),
        };
    }
}
