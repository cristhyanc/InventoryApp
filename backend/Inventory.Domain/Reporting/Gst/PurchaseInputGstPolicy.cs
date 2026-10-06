using Inventory.Domain.Purchases;

namespace Inventory.Domain.Reporting.Gst;

/// <summary>
/// One purchase's GST components as a report sees them: its lines, and its two purchase-level
/// charges. The same shape <see cref="PurchaseGstPolicy.Calculate"/> already takes for a single
/// purchase, named so a period of purchases can be passed around as one list.
/// </summary>
public readonly record struct PurchaseGstComponents(
    IReadOnlyList<PurchaseGstLine> Lines,
    PurchaseGstCharge DeliveryCharge,
    PurchaseGstCharge PackageCharge);

/// <summary>
/// A reporting period's purchase input GST, split the way the GST accounting aid presents it, plus
/// the components the app was never told about. The unresolved pair stays separate from the GST
/// figures so the report can show what is known beside a clear incomplete status.
/// </summary>
public readonly record struct PurchaseInputGstResult(
    decimal LineGst,
    decimal ChargeGst,
    int UnresolvedComponentCount,
    decimal UnresolvedAmount)
{
    /// <summary>The period's total purchase input GST: product lines plus delivery/package charges.</summary>
    public decimal TotalGst => LineGst + ChargeGst;

    /// <summary>Whether every relevant purchase component in the period carries a classification.</summary>
    public bool IsComplete => UnresolvedComponentCount == 0;
}

/// <summary>
/// Aggregates purchase input GST over a reporting period (issue #432, parent issue #62 decision D1).
///
/// It owns only the split and the period sum. Every amount and every rounding decision is
/// <see cref="PurchaseGstPolicy"/>'s, which this policy calls twice per purchase - once over the
/// lines and once over the two charges - rather than restating the <c>round(amount / 11, 2)</c>
/// rule a second time. Because each component is rounded before anything is added, a period total
/// is the sum of already-rounded component amounts and can differ by cents from dividing the
/// period's purchase value by 11; that component-level rounding is the authoritative one
/// (AGENTS.md § Purchase GST classification).
///
/// <c>Unknown</c> components contribute no GST at all and are returned as a count and a
/// GST-inclusive amount, never folded into the GST figures and never collapsed into GST-free.
/// </summary>
public static class PurchaseInputGstPolicy
{
    private static readonly PurchaseGstLine[] NoLines = [];

    /// <summary>
    /// The period's input GST over <paramref name="purchases"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A component carries an unsupported classification.</exception>
    public static PurchaseInputGstResult Calculate(IEnumerable<PurchaseGstComponents> purchases)
    {
        ArgumentNullException.ThrowIfNull(purchases);

        var lineGst = 0m;
        var chargeGst = 0m;
        var unresolvedCount = 0;
        var unresolvedAmount = 0m;

        foreach (var purchase in purchases)
        {
            var lines = PurchaseGstPolicy.Calculate(purchase.Lines ?? NoLines, AbsentCharge, AbsentCharge);
            var charges = PurchaseGstPolicy.Calculate(NoLines, purchase.DeliveryCharge, purchase.PackageCharge);

            lineGst += lines.InputGst;
            chargeGst += charges.InputGst;
            unresolvedCount += lines.UnresolvedComponentCount + charges.UnresolvedComponentCount;
            unresolvedAmount += lines.UnresolvedAmount + charges.UnresolvedAmount;
        }

        return new PurchaseInputGstResult(lineGst, chargeGst, unresolvedCount, unresolvedAmount);
    }

    /// <summary>
    /// The charge passed to the lines-only pass. A null amount is an absent charge, which
    /// <see cref="PurchaseGstPolicy"/> skips entirely, so the two passes partition the purchase's
    /// components exactly once each.
    /// </summary>
    private static PurchaseGstCharge AbsentCharge => new(null, default);
}
