namespace Inventory.Domain.Gst;

/// <summary>
/// One stored purchase line as the historical GST classification maintenance workflow reads it
/// (issue #433): the amounts input GST is derived from, the classification state it carries today,
/// and the product rule that could classify it.
///
/// <see cref="Quantity"/> and <see cref="UnitCost"/> are the persisted line values rather than a
/// pre-computed amount, because the line amount's own rounding is part of the rule
/// (<c>Inventory.Domain.Purchases.PurchaseGstPolicy</c>, parent issue #62 decision D2).
/// </summary>
public readonly record struct HistoricalGstPurchaseLine(
    int LineId,
    long ProductId,
    decimal Quantity,
    decimal UnitCost,
    GstClassificationState State,
    GstClassification ProductRule);

/// <summary>
/// One stored purchase's complete GST state and the rules that could classify it, as the historical
/// classification Preview and Apply read it (issue #433).
///
/// It deliberately carries <em>every</em> component of the purchase, not only the unclassified ones:
/// the already-classified components are what make <see cref="HistoricalGstClassificationFingerprint"/>
/// notice that relevant purchase data changed between a preview and its apply, and the policy is the
/// one place that decides which of them a rule may touch at all.
///
/// <see cref="SupplierDefaults"/> is the purchase supplier's configured defaults, or
/// <see cref="SupplierGstDefaults.None"/> for a purchase with no supplier - an absent supplier has
/// configured nothing, which is not the same as having configured "GST-free".
/// </summary>
public sealed record HistoricalGstPurchase(
    int PurchaseId,
    decimal? DeliveryCost,
    GstClassificationState Delivery,
    decimal? PackageCost,
    GstClassificationState Package,
    SupplierGstDefaults SupplierDefaults,
    IReadOnlyList<HistoricalGstPurchaseLine> Lines);
