namespace Inventory.Application.Products;

/// <summary>
/// The optional search/category narrowing a product listing request carries, plus whether
/// retired products belong in the answer. A null member means "do not narrow on this", exactly as
/// the former <c>InventoryApi.Services.ProductService.GetAll</c>/<c>LowStock</c> query building did.
///
/// <para>Narrowing by supplier is gone (issue #787, A1 of #786): a product has no supplier to
/// narrow on, so the filter cannot offer one.</para>
///
/// <para><paramref name="IncludeRetired"/> is deliberately not that kind of narrowing and has no
/// "unset" value (issue #736): a product whose <c>IsActive</c> is false has been retired, which is
/// what a product with history is given instead of a delete, and the catalogue a person works from
/// does not hold it. Every caller therefore states which catalogue it is reading - the working one
/// or every row the business owns - rather than inheriting whichever the adapter happens to
/// return.</para>
/// </summary>
public sealed record ProductCatalogFilter(
    string? Search, long? CategoryId, bool IncludeRetired)
{
    /// <summary>
    /// The unnarrowed catalogue, <strong>retired products included</strong>: every product the
    /// business owns. It is what a caller that resolves activity itself needs - the home Dashboard
    /// summary and <c>Inventory.Application.Machines.ListMachineProducts</c> both read the whole
    /// catalogue and then apply their own Domain rule to it, and a machine slot or a refill alert
    /// naming a retired product must still be able to describe it.
    /// </summary>
    public static ProductCatalogFilter None { get; } = new(null, null, IncludeRetired: true);
}

/// <summary>
/// Narrow read port for the product catalogue, owned by the Application layer (issue #240). Reads
/// are tenant-scoped centrally by the persistence adapter's own query filters, never by a filter
/// member here: no caller may choose an owning business (AGENTS.md § Tenant ownership).
/// </summary>
public interface IProductCatalogStore
{
    /// <summary>
    /// The filtered catalogue ordered by name, with category, supplier and stock-adjustment detail,
    /// and with each product's <see cref="ProductRecord.DeletionEligibility"/> resolved (issue #737):
    /// this is the listing a person maintains the catalogue from, so it says which of its products
    /// may actually be deleted rather than leaving a client to attempt one that must fail.
    /// </summary>
    Task<IReadOnlyList<ProductRecord>> ListOrderedByNameAsync(
        ProductCatalogFilter filter, CancellationToken cancellationToken);

    /// <summary>
    /// The filtered catalogue in no particular order, for a caller that ranks or enriches the rows
    /// itself (<see cref="ListLowStockProducts"/>, <c>Inventory.Application.Machines.ListMachineProducts</c>,
    /// the home Dashboard summary).
    ///
    /// <para><see cref="ProductRecord.DeletionEligibility"/> is deliberately <strong>not</strong>
    /// resolved here: none of those callers deletes a product, and the answer costs a count of every
    /// kind of row that refers to one. A caller that needs it asks one of the other two reads.</para>
    ///
    /// <para><paramref name="resolveInventoryHistory"/> states, the same way, whether the caller needs
    /// <see cref="ProductRecord.HasInventoryHistory"/> (issue #861): only the home Dashboard summary
    /// aggregates an inventory valuation from this read and needs that evidence to tell a product that
    /// has never held inventory from one whose inventory is unknown, so only it asks for it. Resolving
    /// it costs a query against six other tables for every product that could still be empty, and
    /// <see cref="ListLowStockProducts"/> and the machine product listing never read the result, so
    /// they ask <see langword="false"/> and pay nothing for it - the same reason this is a parameter
    /// stated by every call rather than a default, as <see cref="ProductCatalogFilter.IncludeRetired"/>
    /// already is.</para>
    /// </summary>
    Task<IReadOnlyList<ProductRecord>> ListUnorderedAsync(
        ProductCatalogFilter filter, bool resolveInventoryHistory, CancellationToken cancellationToken);

    /// <summary>
    /// The single product, or <c>null</c> when the caller's business owns no such product.
    ///
    /// <para>A retired product is answered like any other (issue #736). This is the read that keeps
    /// a retired product's history and its reactivation reachable while the listings leave it out:
    /// retirement decides what a listing offers to work with, never what the business may look
    /// at.</para>
    ///
    /// <para><see cref="ProductRecord.DeletionEligibility"/> is resolved here too (issue #737), for
    /// the same reason it is on the ordered listing: this read is the other surface a product is
    /// maintained from.</para>
    /// </summary>
    Task<ProductRecord?> FindAsync(long id, CancellationToken cancellationToken);
}
