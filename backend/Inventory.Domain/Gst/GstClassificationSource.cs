namespace Inventory.Domain.Gst;

/// <summary>
/// How a <see cref="GstClassification"/> was established, persisted alongside it so a historical
/// result stays auditable (parent issue #62's approved GST design, "Classification provenance").
///
/// A manual classification always wins and is never overwritten by a rule. The rule-based sources
/// are declared here because they are part of the settled provenance vocabulary; the rules that
/// produce them arrive with product GST rules and supplier defaults (issue #430) and the explicit
/// historical Preview/Apply maintenance workflow (issue #433).
/// </summary>
public enum GstClassificationSource
{
    /// <summary>No classification has been established. Pairs with <see cref="GstClassification.Unknown"/>.</summary>
    Unknown = 0,

    /// <summary>A person classified this component explicitly.</summary>
    Manual = 1,

    /// <summary>An explicitly configured product-specific GST rule classified this purchase line.</summary>
    ProductRule = 2,

    /// <summary>An explicitly configured supplier default classified this purchase line.</summary>
    SupplierDefault = 3,

    /// <summary>An explicitly configured supplier fee default classified this delivery or package charge.</summary>
    SupplierFeeDefault = 4,
}
