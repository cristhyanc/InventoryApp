using Inventory.Domain.Purchases;

namespace Inventory.Application.Purchases;

/// <summary>
/// The saved purchase's input GST and its unresolved components (issue #431), so a client displays
/// the server's figures instead of deriving GST itself.
///
/// Like <see cref="ComputePurchaseTotalValidation"/>, this owns no formula: it projects a persisted
/// <see cref="PurchaseRecord"/> onto the component inputs of the one authoritative
/// <see cref="PurchaseGstPolicy"/> calculation - every line, plus each of the two charges with its
/// own classification - and returns that policy's answer unchanged. The absent-charge rule, the
/// per-component rounding and the separate unresolved count/amount all stay in the Domain
/// (AGENTS.md § Purchase GST classification).
///
/// It describes what is stored. It is never a projection of an unsaved edit: a caller that has
/// pending changes has to save them before this figure describes them.
/// </summary>
public sealed class ComputePurchaseGstSummary
{
    /// <exception cref="ArgumentOutOfRangeException">A persisted component carries an unsupported classification.</exception>
    public PurchaseGstResult Handle(PurchaseRecord purchase)
    {
        ArgumentNullException.ThrowIfNull(purchase);

        return PurchaseGstPolicy.Calculate(
            purchase.Items.Select(item => new PurchaseGstLine(item.Quantity, item.UnitCost, item.GstClassification)),
            new PurchaseGstCharge(purchase.DeliveryCost, purchase.DeliveryGstClassification),
            new PurchaseGstCharge(purchase.PackageCost, purchase.PackageGstClassification));
    }
}
