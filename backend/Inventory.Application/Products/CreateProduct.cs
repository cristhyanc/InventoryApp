using Inventory.Domain.Products;

namespace Inventory.Application.Products;

/// <summary>
/// The product creation use case: validates the deterministic invariants, then persists. Mirrors
/// the former <c>InventoryApi.Services.ProductService.Create</c> exactly (issue #240).
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
        var costError = ProductInitialCostPolicy.Validate(fields.InitialUnitCost);
        if (costError is not null) return CreateProductResult.Invalid(costError);

        var restockError = ProductRestockPolicy.Validate(fields.LowStockThreshold, fields.RestockTo);
        if (restockError is not null) return CreateProductResult.Invalid(restockError);

        var productId = await _store.CreateAsync(fields, cancellationToken);
        return CreateProductResult.Success(productId);
    }
}
