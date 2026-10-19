using Inventory.Domain.Products;

namespace Inventory.Application.Products;

/// <summary>
/// The product creation use case: validates the deterministic invariants, then persists.
///
/// <para>Issue #736 gave it an HTTP route for the first time (<c>POST /api/products</c>) and the two
/// catalogue invariants a manually created product needs - a name, and a selling price that is not
/// negative (<see cref="ProductCataloguePolicy"/>). The initial-cost and restock rules, their
/// messages and the order they are checked in are unchanged from issue #240.</para>
/// </summary>
public sealed class CreateProduct
{
    private readonly IProductStore _store;

    public CreateProduct(IProductStore store)
    {
        _store = store;
    }

    public async Task<CreateProductResult> Handle(ProductCreateFields fields, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var costError = ProductInitialCostPolicy.Validate(fields.InitialUnitCost);
        if (costError is not null) return CreateProductResult.Invalid(costError);

        var restockError = ProductRestockPolicy.Validate(fields.LowStockThreshold, fields.RestockTo);
        if (restockError is not null) return CreateProductResult.Invalid(restockError);

        var catalogueError = ProductCataloguePolicy.Validate(fields.Name, fields.UnitPrice);
        if (catalogueError is not null) return CreateProductResult.Invalid(catalogueError);

        var attempt = await _store.CreateAsync(fields, cancellationToken);
        return attempt.Outcome == ProductWriteOutcome.Succeeded
            ? CreateProductResult.Success(attempt.ProductId!.Value)
            // The store's own refusal: a category this business does not own. It is a statement
            // about stored data, so only the store can make it.
            : CreateProductResult.Invalid(attempt.Refusal ?? ProductCataloguePolicy.UnknownCategoryMessage);
    }
}
