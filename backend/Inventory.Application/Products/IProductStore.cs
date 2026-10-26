using Inventory.Domain.Gst;

namespace Inventory.Application.Products;

/// <summary>
/// Narrow persistence port for product create/update/delete orchestration, owned by the
/// Application layer.
/// </summary>
public interface IProductStore
{
    /// <summary>
    /// Persists the new product and, when its quantity is positive, the initial restock stock
    /// adjustment and (when an initial unit cost is given) an inventory cost rebuild. Returns the
    /// new product's id.
    ///
    /// <para>Refused when <see cref="ProductCreateFields.CategoryId"/> names a category the caller's
    /// business does not own (issue #736): a product's category is a relationship between two
    /// tenant-owned rows and may not cross a business, and the id a request supplies is the one
    /// place that could be attempted.</para>
    ///
    /// <para>Refused too when <see cref="ProductCreateFields.Unit"/> is blank (issue #895), the same
    /// way a blank name is: the create use case has already applied that rule, and the adapter
    /// applies it again at the write so the column cannot be written blank. An accepted label is
    /// stored trimmed.</para>
    /// </summary>
    Task<ProductWriteAttempt> CreateAsync(ProductCreateFields fields, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(long id, CancellationToken cancellationToken);

    /// <summary>
    /// Updates the product's editable fields, after reading the stored row and deciding the three
    /// refusals that depend on it: a change to a field the business's sales provider manages, and a
    /// category the business does not own (issue #736), and a request that would blank a nonblank
    /// stored unit label (issue #895). The read, the comparison and the write are one operation, so a
    /// product linked to a provider, or given a unit label, between a caller's read and this write is
    /// still refused.
    ///
    /// <para>A product whose stored label is already blank accepts a blank one, which is what keeps
    /// the Products list's activation write - the whole product sent back with <c>isActive</c>
    /// flipped - working for a legacy row (<c>ProductCataloguePolicy.UnitChangeRefusal</c>). An
    /// accepted nonblank label is stored trimmed.</para>
    ///
    /// <para><see cref="ProductWriteOutcome.NotFound"/> is the authoritative not-found answer: an
    /// earlier <see cref="ExistsAsync"/> result is only a read, and the row can be deleted by
    /// another request between that read and this write.</para>
    /// </summary>
    Task<ProductWriteAttempt> UpdateAsync(
        long id, ProductUpdateFields fields, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the product only while nothing refers to it and no sales provider manages it
    /// (issue #736, <c>Inventory.Domain.Products.ProductDeletionPolicy</c>); otherwise it reports
    /// that policy's refusal and writes nothing.
    ///
    /// <para>The counting and the delete are one operation for the same reason the update's
    /// comparison is: two of the references - stock movements and machine stock counts - cascade, so
    /// a product that gained history between a separate count and this delete would have it erased
    /// instead of being refused.</para>
    /// </summary>
    Task<ProductWriteAttempt> DeleteAsync(long id, CancellationToken cancellationToken);

    /// <summary>
    /// The product's configured GST rule (issue #430), or <c>null</c> when no product with
    /// <paramref name="id"/> is visible to the caller's business. A product that has never been
    /// given a rule answers <see cref="GstRules.None"/>, which is a configured state of its own and
    /// not a missing product.
    /// </summary>
    Task<GstClassification?> FindGstRuleAsync(long id, CancellationToken cancellationToken);

    /// <summary>
    /// Stores the product's GST rule and nothing else: no purchase, purchase line, stock movement,
    /// unit cost, costing quantity or inventory value is touched (AGENTS.md § Purchase GST
    /// classification). Returns <c>false</c> when no product with <paramref name="id"/> was updated.
    /// </summary>
    Task<bool> SetGstRuleAsync(long id, GstClassification rule, CancellationToken cancellationToken);
}
