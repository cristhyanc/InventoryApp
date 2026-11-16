namespace Inventory.Domain.Purchases;

/// <summary>
/// When a purchase being entered looks like one that is already recorded (issue #1247).
///
/// <para><strong>It warns and never blocks.</strong> Three facts matching - the same supplier, the
/// same non-empty invoice number and the same business date - is strong evidence that the same
/// receipt is being entered twice, and it is still not proof: a supplier may reuse a number, and a
/// business may legitimately record the same document twice for a reason this application cannot
/// see. So the answer is something to show the operator, who saves anyway or does not. Nothing here
/// refuses a purchase, and the idempotency key - which <em>does</em> refuse one - is a different
/// rule about a different thing: that one is about one submission arriving twice, this one is about
/// two submissions describing one receipt.</para>
///
/// <para><strong>All three have to be there, and the invoice number has to be real.</strong> A
/// purchase with no supplier, or none of the supplier's own document number, carries nothing to
/// recognise a repeat by: warning on "same date" alone would fire on every second purchase of a
/// normal delivery day and would teach an operator to ignore the warning entirely. A blank invoice
/// number is no invoice number (<see cref="PurchaseInvoiceNumberPolicy"/>), so it is never matched
/// against another blank.</para>
///
/// <para>The number is compared exactly, on the value
/// <see cref="PurchaseInvoiceNumberPolicy.Normalize"/> produces - the same value the purchase would
/// be stored with - so " INV-1 " recognises a stored "INV-1". Nothing else about it is folded: the
/// case and the punctuation are the supplier's own, and deciding that "inv-1" and "INV-1" are one
/// invoice is a guess about a supplier's numbering rather than a fact about this receipt.</para>
/// </summary>
public static class PurchaseDuplicateWarningPolicy
{
    /// <summary>
    /// The invoice number to look for, or <see langword="null"/> when this purchase carries nothing
    /// a repeat could be recognised by - no supplier, or no invoice number at all - and no lookup
    /// should be made.
    /// </summary>
    public static string? MatchableInvoiceNumber(int? supplierId, string? invoiceNumber) =>
        supplierId is null ? null : PurchaseInvoiceNumberPolicy.Normalize(invoiceNumber);
}
