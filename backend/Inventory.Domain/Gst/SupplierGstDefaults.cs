namespace Inventory.Domain.Gst;

/// <summary>
/// A supplier's explicitly configured GST defaults (issue #430): one for its product lines, and a
/// separate one for each of the two purchase-level charges that exist, delivery and package (parent
/// issue #62, "Fee types").
///
/// The three are kept apart because a supplier may legitimately sell GST-free goods and still
/// charge GST on delivery, so a charge never inherits the product-line default. All three default
/// to <see cref="GstRules.None"/>: a supplier being GST-registered, or GST appearing somewhere on
/// its invoice, is not a configured default - the whole point of the approved design is that a
/// supplier default is explicit configuration rather than inference.
/// </summary>
public readonly record struct SupplierGstDefaults(
    GstClassification ProductLines,
    GstClassification Delivery,
    GstClassification Package)
{
    /// <summary>No configured default of any kind: what every supplier starts with.</summary>
    public static SupplierGstDefaults None => default;

    /// <summary>
    /// Whether any of the three submitted values is outside the declared vocabulary. Callers check
    /// this before anything is stored, so a rule no policy describes never reaches the database.
    /// </summary>
    public bool HasUnsupportedRule =>
        !GstRules.IsSupportedRule(ProductLines) ||
        !GstRules.IsSupportedRule(Delivery) ||
        !GstRules.IsSupportedRule(Package);
}
