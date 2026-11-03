namespace Inventory.Application.Products;

/// <summary>
/// What a catalogue write to the product store did (issue #736).
/// </summary>
public enum ProductWriteOutcome
{
    /// <summary>The product was created, updated or deleted.</summary>
    Succeeded,

    /// <summary>No product with that id is visible to the caller's business.</summary>
    NotFound,

    /// <summary>
    /// The store read the product's current state and refused the write, with a message a caller may
    /// be shown: a provider-managed field was being changed, the category named does not belong to
    /// this business, or history still refers to the product being deleted.
    /// </summary>
    Refused,

    /// <summary>
    /// The selected master product a create was to be linked to does not stand (issue #1051): it is
    /// no longer in the shared catalogue, the submitted Name or unit label is no longer its own, or a
    /// Super Admin has ruled that pair out. <strong>Nothing is written</strong> - not the product
    /// either, because the operator asked for a linked product and this is not one.
    ///
    /// <para>It is a conflict rather than a validation failure, and it carries
    /// <see cref="ProductWriteAttempt.ConflictCode"/> because a client has to tell the three apart to
    /// offer "choose again" or "create without the link": the submitted values are perfectly valid,
    /// and what has changed is the state they were chosen against.</para>
    /// </summary>
    MasterProductConflict,
}

/// <summary>
/// The outcome of one catalogue write, including the refusal the store decided on its own authority
/// (issue #736).
///
/// <para>The refusals the store reports are the ones that depend on the <em>stored</em> row - which
/// provider-managed fields it carries, which categories the business owns, what already refers to
/// it - so the read they are decided from and the write they guard are one operation inside the
/// adapter rather than a use-case read that a concurrent change can invalidate. Deterministic
/// validation of the submitted values stays in the use case, where it needs no database at all.</para>
/// </summary>
/// <param name="Outcome">What the write did.</param>
/// <param name="ProductId">The created product's key, for a create that succeeded.</param>
/// <param name="Refusal">The caller-safe sentence of a refusal or a conflict.</param>
/// <param name="ConflictCode">
/// The stable code of a <see cref="ProductWriteOutcome.MasterProductConflict"/> and of nothing else
/// (issue #1051): one of <c>Inventory.Domain.PriceCatalogue.MasterProductSelectionPolicy</c>'s three.
/// A client branches on this rather than on the sentence beside it, which may be reworded.
/// </param>
public sealed record ProductWriteAttempt(
    ProductWriteOutcome Outcome,
    long? ProductId,
    string? Refusal,
    string? ConflictCode = null)
{
    public static ProductWriteAttempt Succeeded(long? productId = null) =>
        new(ProductWriteOutcome.Succeeded, productId, null);

    public static ProductWriteAttempt NotFound() => new(ProductWriteOutcome.NotFound, null, null);

    public static ProductWriteAttempt Refused(string refusal) =>
        new(ProductWriteOutcome.Refused, null, refusal);

    /// <summary>
    /// The selected master product does not stand, with the code a client branches on and the
    /// sentence it shows. Nothing was written.
    /// </summary>
    public static ProductWriteAttempt MasterProductConflict(string code, string message) =>
        new(ProductWriteOutcome.MasterProductConflict, null, message, code);
}
