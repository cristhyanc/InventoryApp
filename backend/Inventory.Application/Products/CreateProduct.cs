using Inventory.Domain.Products;

namespace Inventory.Application.Products;

/// <summary>
/// The product creation use case: validates the deterministic invariants, then persists.
///
/// <para>Issue #736 gave it an HTTP route for the first time (<c>POST /api/products</c>) and the
/// catalogue invariants a manually created product needs (<see cref="ProductCataloguePolicy"/>):
/// a name, and since issue #895 a unit label. The selling price it also bounded is gone with the
/// field. The restock rule, the messages and the order the rules are checked in are unchanged from
/// issue #240; the initial-cost rule gained its second field in issue #1238 and is now decided from
/// the opening stock beside the cost (<see cref="ProductInitialCostPolicy"/>).</para>
///
/// <para>The unit label is checked here because a create's answer depends on nothing but the
/// request: a new product has no stored label to preserve. The <em>update</em>'s unit rule is the
/// other half of issue #895 and is deliberately not here - it compares the stored label, so it is
/// decided inside the write's own transaction (<see cref="IProductStore.UpdateAsync"/>), the same
/// place the provider-managed and category refusals are.</para>
///
/// <para><strong>The master product a create may be linked to is decided there too</strong>
/// (issue #1051): whether that master product still exists, whether the submitted Name and unit
/// label are still its own, and whether a Super Admin has ruled the pair out are three statements
/// about <em>stored</em> rows, so the read, the comparison and both writes are one operation inside
/// the store's transaction. This use case only reports the conflict it answers with, and the
/// deterministic refusals above still come first: a product that would not be created at all is
/// never asked which master product it is.</para>
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

        // Both fields, since issue #1238: opening stock above zero is recorded as a costed opening
        // movement, so the cost it would be recorded with is required here - before anything is
        // saved - rather than left to freeze the product's later stock writes.
        var costError = ProductInitialCostPolicy.Validate(fields.QuantityInStock, fields.InitialUnitCost);
        if (costError is not null) return CreateProductResult.Invalid(costError);

        var restockError = ProductRestockPolicy.Validate(fields.LowStockThreshold, fields.RestockTo);
        if (restockError is not null) return CreateProductResult.Invalid(restockError);

        var catalogueError = ProductCataloguePolicy.Validate(fields.Name)
            ?? ProductCataloguePolicy.ValidateUnit(fields.Unit);
        if (catalogueError is not null) return CreateProductResult.Invalid(catalogueError);

        var attempt = await _store.CreateAsync(fields, cancellationToken);
        return attempt.Outcome switch
        {
            ProductWriteOutcome.Succeeded => CreateProductResult.Success(attempt.ProductId!.Value),
            // The selected master product does not stand, and nothing at all was created (issue
            // #1051). The code travels with the sentence because a client has to tell the three
            // conflicts apart to offer "choose again" or "create without the link".
            ProductWriteOutcome.MasterProductConflict => CreateProductResult.Conflict(
                attempt.ConflictCode!,
                attempt.Refusal!),
            // The store's own refusal: a category this business does not own. It is a statement
            // about stored data, so only the store can make it.
            _ => CreateProductResult.Invalid(
                attempt.Refusal ?? ProductCataloguePolicy.UnknownCategoryMessage),
        };
    }
}
