using Inventory.Domain.Purchases;

namespace Inventory.Domain.Gst;

/// <summary>
/// How a <b>new</b> purchase's components are classified when it is created (issue #1348, which
/// reverses parent issue #62's decision D4 for new purchases only).
///
/// A component the person classified - including an explicit <see cref="GstClassification.Unknown"/>,
/// which is a person saying "not classified" - is recorded exactly as
/// <see cref="PurchaseGstPolicy.Classify"/> has always recorded it. A component submitted with no
/// classification at all is classified from the configured rules with the one precedence
/// <see cref="HistoricalGstClassificationPolicy.Resolve"/> owns: the product's own rule, then the
/// supplier's product-line default for a line; only the supplier's matching fee default for a
/// delivery or package charge. A component no rule covers stays unclassified.
///
/// It applies to a create only. An edit never reads a rule: an omitted classification there keeps
/// the stored one, and already-recorded purchases are reached by rules only through the Admin
/// Preview then Apply workflow (issue #433).
/// </summary>
public static class NewPurchaseGstClassificationPolicy
{
    /// <summary>The state a new purchase line is stored with.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A submitted value or a rule is not a declared classification.</exception>
    public static GstClassificationState ClassifyLine(
        GstClassification? requested,
        GstClassification productRule,
        SupplierGstDefaults supplierDefaults) =>
        requested is null
            ? HistoricalGstClassificationPolicy.Resolve(GstComponentKind.ProductLine, productRule, supplierDefaults)
            : PurchaseGstPolicy.Classify(requested);

    /// <summary>
    /// The state a new purchase's delivery or package charge is stored with. An absent or zero
    /// charge carries no classification at all, whatever was submitted or configured (decision D3).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="kind"/> is not a charge, or a submitted value or default is not a declared classification.
    /// </exception>
    public static GstClassificationState ClassifyCharge(
        GstComponentKind kind,
        decimal? amount,
        GstClassification? requested,
        SupplierGstDefaults supplierDefaults)
    {
        if (kind is not (GstComponentKind.DeliveryCharge or GstComponentKind.PackageCharge))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a purchase charge.");

        var state = requested is null
            ? HistoricalGstClassificationPolicy.Resolve(kind, GstRules.None, supplierDefaults)
            : PurchaseGstPolicy.Classify(requested);

        return PurchaseGstPolicy.ClassifyCharge(amount, state);
    }
}
