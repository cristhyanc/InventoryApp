using Inventory.Domain.Purchases;

namespace Inventory.Application.Purchases;

/// <summary>
/// The one place the purchase create and edit use cases refuse an invoice number that is too long
/// and normalise the one they accept (issue #1162).
///
/// It runs before any state changes: before <see cref="UploadPurchase"/> saves the uploaded
/// document, and before <see cref="UpdatePurchase"/> hands the edit to the store's transaction. So an
/// over-long number - which a multipart form field can carry - leaves the purchase, its lines, its
/// stock movements and its stored document exactly as they were, and the stored value is the trimmed
/// one every read reports.
///
/// The decisions themselves are the Domain's
/// (<see cref="PurchaseInvoiceNumberPolicy"/>); this only applies them from both paths, so the two
/// cannot drift apart.
/// </summary>
public static class PurchaseInvoiceNumberSubmission
{
    /// <summary>
    /// <paramref name="fields"/> with its invoice number trimmed, or <c>null</c> when nothing but
    /// whitespace was submitted - which is how a create records none and an edit clears one.
    /// </summary>
    /// <exception cref="InvalidOperationException">The submitted invoice number is too long.</exception>
    public static PurchaseFields Normalized(PurchaseFields fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        if (!PurchaseInvoiceNumberPolicy.IsWithinLimit(fields.InvoiceNumber))
            throw new InvalidOperationException(PurchaseInvoiceNumberPolicy.TooLongMessage);

        return fields with { InvoiceNumber = PurchaseInvoiceNumberPolicy.Normalize(fields.InvoiceNumber) };
    }
}
