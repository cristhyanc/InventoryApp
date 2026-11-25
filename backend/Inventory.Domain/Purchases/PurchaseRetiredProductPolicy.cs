namespace Inventory.Domain.Purchases;

/// <summary>
/// Whether a retired product may appear on a purchase (issue #1249, audit #1222 finding INV-14).
/// <c>IsActive</c> is how a product is taken out of use (<c>Inventory.Domain.Products</c>'s
/// retirement rule, see docs/architecture.md § Data flow), and a retired product must not be
/// offered to purchase from: a new purchase, or a new line added to an existing one, is refused
/// outright.
///
/// <para><strong>An existing line already naming a retired product is different.</strong> It is
/// part of the history retirement must never move, so the edit screen still has to let an operator
/// remove it, lower its quantity, or correct its cost - none of which adds to what a retired
/// product moved. Only an increase would do that, which is the one submission this policy refuses:
/// <see cref="IsExistingLineQuantityAllowed"/> is what a store applies the rule with, over the
/// quantity it already read and the quantity the caller submitted.</para>
/// </summary>
public static class PurchaseRetiredProductPolicy
{
    /// <summary>What a caller is told when a purchase (or a new line added to one) names a retired product.</summary>
    public static string NewLineRefusalMessage(string productName) =>
        $"'{productName}' is retired and cannot be purchased.";

    /// <summary>
    /// What a caller is told when an edit raises the quantity of an existing line that already
    /// names a retired product.
    /// </summary>
    public static string QuantityIncreaseRefusalMessage(string productName) =>
        $"'{productName}' is retired: its quantity on this purchase cannot be increased.";

    /// <summary>
    /// Whether <paramref name="requestedQuantity"/> is allowed for an existing line naming a
    /// retired product, given the quantity it already carries. A decrease, an unchanged quantity,
    /// or a cost-only correction (quantity unchanged) are all allowed; only an increase is refused.
    /// </summary>
    public static bool IsExistingLineQuantityAllowed(decimal storedQuantity, decimal requestedQuantity) =>
        requestedQuantity <= storedQuantity;
}
