using Inventory.Domain.Gst;

namespace Inventory.Application.Purchases;

/// <summary>
/// A requested purchase line item, as submitted by the caller before validation.
///
/// <see cref="GstClassification"/> is nullable for the same reason the purchase's charge
/// classifications are (issue #429): on an edit, a <c>null</c> keeps the line's stored
/// classification, while an explicit value replaces it and is persisted with provenance
/// <see cref="GstClassificationSource.Manual"/>. The caller never submits the provenance itself.
///
/// <see cref="Id"/> is that stored line's stable identity, as returned by the purchase read. It is
/// optional: a create ignores it, and an edit that omits it falls back to matching by product, the
/// way every client did before. Supplying it is what lets an edit of a purchase holding several
/// lines for one product keep each line's classification on the right line
/// (<c>Inventory.Domain.Purchases.PurchaseLineIdentityPolicy</c>).
/// </summary>
public sealed record PurchaseItemInput(
    long ProductId,
    decimal Quantity,
    decimal UnitCost,
    GstClassification? GstClassification = null,
    int? Id = null);
