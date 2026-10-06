using Inventory.Domain.Purchases;

namespace Inventory.Application.Purchases;

/// <summary>
/// The one place the purchase create and edit use cases reject a request whose submitted GST
/// classifications are outside the supported vocabulary (issue #429).
///
/// It runs before any state changes: before <see cref="UploadPurchase"/> saves the uploaded
/// document, and before <see cref="UpdatePurchase"/> hands the edit to the store's transaction. So
/// an undefined classification - which a multipart form field or a deserialized JSON number can
/// carry - leaves the purchase, the inventory and the stored document exactly as they were.
///
/// The decision itself is the Domain's
/// (<see cref="PurchaseGstPolicy.HasUnsupportedClassification"/>); this only calls it from both
/// paths, so the two cannot drift apart.
/// </summary>
public static class PurchaseGstSubmission
{
    /// <summary>
    /// Throws when either charge classification, or any submitted line's, is not a supported value.
    /// </summary>
    /// <exception cref="InvalidOperationException">A submitted classification is unsupported.</exception>
    public static void EnsureClassificationsAreSupported(
        PurchaseFields fields,
        IReadOnlyList<PurchaseItemInput>? items)
    {
        ArgumentNullException.ThrowIfNull(fields);

        if (PurchaseGstPolicy.HasUnsupportedClassification(
                fields.DeliveryGstClassification,
                fields.PackageGstClassification,
                (items ?? []).Select(item => item.GstClassification)))
        {
            throw new InvalidOperationException(PurchaseGstPolicy.UnsupportedClassificationMessage);
        }
    }
}
