using Inventory.Domain.Gst;

namespace Inventory.Application.Purchases;

/// <summary>
/// A purchase's editable, non-item fields, as submitted by the caller.
///
/// The two GST classifications are nullable because "not submitted" and "submitted as unknown" are
/// different requests on an edit (issue #429): a <c>null</c> leaves the stored classification of
/// that charge alone, while an explicit value replaces it. A submitted classification is persisted
/// with provenance <see cref="GstClassificationSource.Manual"/>, and a charge that is absent or
/// zero carries no classification at all - both decided by
/// <c>Inventory.Domain.Purchases.PurchaseGstPolicy</c>.
/// </summary>
public sealed record PurchaseFields(
    string? Title,
    string? Notes,
    decimal? TotalAmount,
    decimal? DeliveryCost,
    decimal? PackageCost,
    DateTime? PurchaseDate,
    int? SupplierId,
    GstClassification? DeliveryGstClassification = null,
    GstClassification? PackageGstClassification = null);
