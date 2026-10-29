namespace Inventory.Application.Purchases;

/// <summary>
/// What the "attach only if empty" write found when it took the write lock and read the purchase
/// (issue #976). There are exactly three answers, and each is a different thing for the API to say:
/// the document was recorded, there is no such purchase of the caller's business, or that purchase
/// already has a document and this one must not replace it.
/// </summary>
public enum PurchaseDocumentAttachOutcome
{
    /// <summary>The purchase had no document and now has this one.</summary>
    Attached,

    /// <summary>
    /// No purchase with that id is visible to the caller's business - which, through the tenant
    /// query filters, is the same answer for another business's purchase as for one that was never
    /// recorded.
    /// </summary>
    PurchaseNotFound,

    /// <summary>
    /// The purchase already had a document when the write read it. Replacing one is deliberately
    /// out of scope, and this is what the loser of two concurrent attaches is told.
    /// </summary>
    AlreadyHasDocument,
}

/// <summary>
/// The result of an attach: its <paramref name="Outcome"/> and, only when that is
/// <see cref="PurchaseDocumentAttachOutcome.Attached"/>, the purchase as it now stands.
/// </summary>
public sealed record PurchaseDocumentAttachment(
    PurchaseDocumentAttachOutcome Outcome, PurchaseRecord? Purchase);
