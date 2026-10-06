using Inventory.Domain.Gst;

namespace Inventory.Application.Purchases;

/// <summary>
/// A requested purchase line item, as submitted by the caller before validation.
///
/// <see cref="GstClassification"/> is nullable for the same reason the purchase's charge
/// classifications are (issue #429): on an edit, a <c>null</c> keeps the line's stored
/// classification, while an explicit value replaces it and is persisted with provenance
/// <see cref="GstClassificationSource.Manual"/>. The caller never submits the provenance itself.
/// </summary>
public sealed record PurchaseItemInput(
    long ProductId,
    decimal Quantity,
    decimal UnitCost,
    GstClassification? GstClassification = null);
