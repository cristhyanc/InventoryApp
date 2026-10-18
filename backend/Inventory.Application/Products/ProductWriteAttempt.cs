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
public sealed record ProductWriteAttempt(ProductWriteOutcome Outcome, long? ProductId, string? Refusal)
{
    public static ProductWriteAttempt Succeeded(long? productId = null) =>
        new(ProductWriteOutcome.Succeeded, productId, null);

    public static ProductWriteAttempt NotFound() => new(ProductWriteOutcome.NotFound, null, null);

    public static ProductWriteAttempt Refused(string refusal) =>
        new(ProductWriteOutcome.Refused, null, refusal);
}
