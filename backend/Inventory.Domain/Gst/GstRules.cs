namespace Inventory.Domain.Gst;

/// <summary>
/// The GST rule vocabulary an administrator configures on a product and on a supplier (issue #430),
/// and the one place "no rule at all" is named.
///
/// A rule reuses <see cref="GstClassification"/> rather than declaring a second enum: a rule says
/// which classification a component would take, so a separate vocabulary could only drift from the
/// one <c>Inventory.Domain.Purchases.PurchaseGstPolicy</c> calculates with. <see cref="None"/> is
/// therefore <see cref="GstClassification.Unknown"/>, and it stays a distinct state: "nobody has
/// configured a rule" is not "GST-free", and it is the value every existing product and supplier
/// starts at.
///
/// Configuring a rule classifies nothing by itself. The approved GST design of parent issue #62
/// settles the precedence a rule is later read with - a manual classification always wins, then a
/// product rule, then an explicitly configured supplier default (and a supplier fee default for a
/// delivery or package charge), and anything no rule deterministically covers stays
/// <see cref="GstClassification.Unknown"/> - but applying it to purchase data is the explicit
/// historical Preview/Apply maintenance workflow (issue #433), never this configuration.
///
/// Like <see cref="GstClassifications"/>, this is also a validation boundary: a rule arrives from a
/// JSON body as an arbitrary integer, and a value outside the declared vocabulary is rejected rather
/// than stored as a rule no policy describes.
/// </summary>
public static class GstRules
{
    /// <summary>
    /// No configured rule. Spelled out because the absence of a rule is a real, persisted,
    /// meaningful state here, not a placeholder: it is what leaves a component for a person or for
    /// a lower-precedence rule to classify.
    /// </summary>
    public const GstClassification None = GstClassification.Unknown;

    /// <summary>The message an unsupported rule value is rejected with.</summary>
    public static string UnsupportedRuleMessage => GstClassifications.UnsupportedMessage;

    /// <summary>Whether <paramref name="rule"/> is one of the declared classifications.</summary>
    public static bool IsSupportedRule(GstClassification rule) => GstClassifications.IsSupported(rule);
}
