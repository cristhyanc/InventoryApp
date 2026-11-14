using Inventory.Application.Time;

namespace Inventory.Application.Purchases;

/// <summary>
/// The purchase edit use case. The field/item validation, the supplier-order fulfillment
/// reconciliation, the stock-movement diffing, and the resulting inventory-cost rebuild are a
/// single transaction against the tracked purchase entity graph, so they stay in
/// <see cref="IPurchaseStore"/>'s EF-owned implementation (docs/architecture.md's Purchasing and
/// costing slice), the same carve-out the categories/suppliers and operating-expenses slices used
/// for their own EF-coupled writes.
///
/// <para><strong>Since issue #1246 every save needs a date of today or earlier</strong> in the
/// business's own time zone, this one included. An edit may submit no date, in which case the
/// stored date is the one that would stand, so that is the date judged - which is what makes
/// correcting the date the only way to save an existing future-dated purchase.</para>
/// </summary>
public sealed class UpdatePurchase
{
    private readonly IPurchaseStore _store;
    private readonly IBusinessCalendar _calendar;

    public UpdatePurchase(IPurchaseStore store, IBusinessCalendar calendar)
    {
        _store = store;
        _calendar = calendar;
    }

    /// <exception cref="InvalidOperationException">
    /// A submitted GST classification is unsupported, the submitted invoice number is too long, the
    /// purchase date is later than today, or the store rejects the edit.
    /// </exception>
    public async Task<PurchaseRecord?> Handle(
        int id, PurchaseFields fields, IReadOnlyList<PurchaseItemInput>? items, CancellationToken cancellationToken)
    {
        // Checked here rather than in the store, so an unsupported classification is refused before
        // the edit's transaction opens and nothing is written (issue #429).
        PurchaseGstSubmission.EnsureClassificationsAreSupported(fields, items);

        // Likewise for the invoice number (issue #1162): an over-long one is refused before the
        // transaction opens, and the accepted one reaches the store trimmed, with blank as none - so
        // an edit that clears it stores no number rather than an empty string.
        var submitted = PurchaseInvoiceNumberSubmission.Normalized(fields);

        // Likewise for the purchase date (issue #1246). The date that would stand after this save is
        // the submitted one, or the stored one when the request submits none - so an edit of a
        // purchase dated in the future is refused until its date is corrected, whichever of the two
        // carries that date. Reading the stored date before the store's transaction opens cannot let
        // a future-dated save through: the only writes that can change it in between obey this same
        // rule, so the stored date they can leave is today's or earlier.
        var effectivePurchaseDate = submitted.PurchaseDate
            ?? (await _store.FindByIdAsync(id, cancellationToken))?.PurchaseDate;
        if (effectivePurchaseDate is { } purchaseDate)
            PurchaseDateSubmission.EnsureNotAfterToday(purchaseDate, _calendar);

        return await _store.UpdateAsync(id, submitted, items, cancellationToken);
    }
}
