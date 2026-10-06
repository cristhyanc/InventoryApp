using Inventory.Domain.Purchases;
using Inventory.Domain.Reporting.Gst;

namespace Inventory.Domain.Gst;

/// <summary>
/// The one authoritative rule for classifying already-recorded purchase components from configured
/// product GST rules and supplier GST defaults (issue #433, under the approved GST design of parent
/// issue #62).
///
/// It is deterministic and total: given the stored purchases it returns both the summary a person
/// approves and the exact component writes that summary stands for. That is what lets the Preview
/// and the Apply share one calculation - the Apply recomputes this plan from its own authoritative
/// read inside its transaction, so a caller never submits a classification, a provenance or a GST
/// figure.
///
/// Three rules do all the work, and each of them is a settled decision rather than an
/// implementation detail:
/// <list type="bullet">
///   <item><b>Only unclassified components are examined.</b> A manual classification always wins and
///   is never overwritten, and neither is an earlier rule-based one: re-running Preview and Apply
///   after an Apply therefore changes nothing (AGENTS.md § Product GST rules and supplier
///   defaults).</item>
///   <item><b>Precedence</b> for a product line is the product's own rule, then the supplier's
///   product-line default. A delivery or package charge reads only the supplier's matching fee
///   default - never a product rule, and never the product-line default, because a supplier may
///   legitimately sell GST-free goods and still charge GST on delivery.</item>
///   <item><b>No rule is not a classification.</b> A component <see cref="GstRules.None"/> leaves
///   uncovered stays <see cref="GstClassification.Unknown"/> and is reported as staying unknown, so
///   the history stays visibly incomplete instead of being quietly treated as GST-free.</item>
/// </list>
///
/// It owns no amount and no rounding rule. The GST a plan would make available comes from
/// <see cref="PurchaseInputGstPolicy"/>, over the newly classified components only, which in turn
/// delegates every amount and rounding decision to <see cref="PurchaseGstPolicy"/> - so the figure
/// an operator approves here is produced by exactly the calculation the GST accounting aid reports
/// with.
/// </summary>
public static class HistoricalGstClassificationPolicy
{
    /// <summary>A charge the plan does not examine, which <see cref="PurchaseGstPolicy"/> skips entirely.</summary>
    private static PurchaseGstCharge AbsentCharge => new(null, default);

    /// <summary>
    /// Whether a rule may classify a component in this state at all: only one that carries no
    /// classification yet.
    ///
    /// Both halves matter. A <see cref="GstClassificationSource.Manual"/> component is never
    /// reclassified, which is the settled decision. And a component whose stored classification is
    /// already <see cref="GstClassification.Taxable"/> or <see cref="GstClassification.GstFree"/> is
    /// not reclassified either, whatever its provenance, so an Apply is idempotent and a later rule
    /// change never silently restates recorded bookkeeping.
    /// </summary>
    public static bool IsReclassifiable(GstClassificationState state) =>
        state.Classification == GstClassification.Unknown && state.Source == GstClassificationSource.Unknown;

    /// <summary>
    /// The state the configured rules give one unclassified component, or
    /// <see cref="GstClassificationState.Unclassified"/> when no rule covers it.
    ///
    /// <paramref name="productRule"/> is ignored for a delivery or package charge, and the fee
    /// default is selected from <paramref name="supplierDefaults"/> by
    /// <paramref name="kind"/> here rather than by the caller, so "a charge never inherits the
    /// product-line default" is decided in one place.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A rule is not a declared classification, or <paramref name="kind"/> is not a declared kind.
    /// </exception>
    public static GstClassificationState Resolve(
        GstComponentKind kind,
        GstClassification productRule,
        SupplierGstDefaults supplierDefaults)
    {
        GstClassifications.Require(productRule);
        var supplierDefault = GstClassifications.Require(kind switch
        {
            GstComponentKind.ProductLine => supplierDefaults.ProductLines,
            GstComponentKind.DeliveryCharge => supplierDefaults.Delivery,
            GstComponentKind.PackageCharge => supplierDefaults.Package,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a declared GST component kind."),
        });

        if (kind == GstComponentKind.ProductLine && productRule != GstRules.None)
            return new GstClassificationState(productRule, GstClassificationSource.ProductRule);

        if (supplierDefault == GstRules.None)
            return GstClassificationState.Unclassified;

        return new GstClassificationState(
            supplierDefault,
            kind == GstComponentKind.ProductLine
                ? GstClassificationSource.SupplierDefault
                : GstClassificationSource.SupplierFeeDefault);
    }

    /// <summary>
    /// The complete plan over <paramref name="purchases"/>: what each unclassified component would
    /// become, how much input GST that would make available, and the component writes it stands for.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A rule is not a declared classification.</exception>
    public static HistoricalGstClassificationPlan Plan(IEnumerable<HistoricalGstPurchase> purchases)
    {
        ArgumentNullException.ThrowIfNull(purchases);

        var changes = new List<HistoricalGstClassificationChange>();
        var examined = new List<PurchaseGstComponents>();
        var lines = new ComponentTally();
        var delivery = new ComponentTally();
        var package = new ComponentTally();
        var purchasesExamined = 0;

        foreach (var purchase in purchases)
        {
            var examinedLines = new List<PurchaseGstLine>();

            foreach (var line in purchase.Lines ?? [])
            {
                if (!IsReclassifiable(line.State))
                    continue;

                var resolved = Resolve(GstComponentKind.ProductLine, line.ProductRule, purchase.SupplierDefaults);
                lines.Count(resolved);
                examinedLines.Add(new PurchaseGstLine(line.Quantity, line.UnitCost, resolved.Classification));
                Record(changes, GstComponentKind.ProductLine, purchase.PurchaseId, line.LineId, resolved);
            }

            var deliveryCharge = ExamineCharge(
                GstComponentKind.DeliveryCharge, purchase.DeliveryCost, purchase.Delivery, purchase, delivery, changes);
            var packageCharge = ExamineCharge(
                GstComponentKind.PackageCharge, purchase.PackageCost, purchase.Package, purchase, package, changes);

            if (examinedLines.Count == 0 && deliveryCharge.Amount is null && packageCharge.Amount is null)
                continue;

            purchasesExamined++;
            examined.Add(new PurchaseGstComponents(examinedLines, deliveryCharge, packageCharge));
        }

        var gst = PurchaseInputGstPolicy.Calculate(examined);
        var summary = new HistoricalGstClassificationSummary(
            lines.Totals,
            delivery.Totals,
            package.Totals,
            purchasesExamined,
            gst.LineGst,
            gst.ChargeGst,
            gst.UnresolvedAmount);

        return new HistoricalGstClassificationPlan(summary, changes);
    }

    /// <summary>
    /// Examines one purchase-level charge, recording its outcome, and returns the charge to pass to
    /// the GST calculation - absent when the charge is not examined.
    ///
    /// A null or zero charge does not exist, so it has no classification, is never eligible and is
    /// never counted as staying unknown (parent issue #62, decision D3). That rule is
    /// <see cref="PurchaseGstPolicy.IsChargePresent"/>'s, not a second copy of it.
    /// </summary>
    private static PurchaseGstCharge ExamineCharge(
        GstComponentKind kind,
        decimal? amount,
        GstClassificationState state,
        HistoricalGstPurchase purchase,
        ComponentTally tally,
        List<HistoricalGstClassificationChange> changes)
    {
        if (!PurchaseGstPolicy.IsChargePresent(amount) || !IsReclassifiable(state))
            return AbsentCharge;

        var resolved = Resolve(kind, GstRules.None, purchase.SupplierDefaults);
        tally.Count(resolved);
        Record(changes, kind, purchase.PurchaseId, lineId: null, resolved);
        return new PurchaseGstCharge(amount, resolved.Classification);
    }

    /// <summary>
    /// Records a write for a component a rule actually classified. A component that stays unknown
    /// produces no change at all: there is nothing to persist, and writing
    /// <c>Unknown</c>/<c>Unknown</c> back over itself would claim a rule had decided something.
    /// </summary>
    private static void Record(
        List<HistoricalGstClassificationChange> changes,
        GstComponentKind kind,
        int purchaseId,
        int? lineId,
        GstClassificationState resolved)
    {
        if (resolved.Classification == GstClassification.Unknown)
            return;

        changes.Add(new HistoricalGstClassificationChange(
            kind, purchaseId, lineId, resolved.Classification, resolved.Source));
    }

    /// <summary>The running per-kind outcome counts, so the three kinds are tallied identically.</summary>
    private sealed class ComponentTally
    {
        private int _examined;
        private int _taxable;
        private int _gstFree;
        private int _unknown;

        public HistoricalGstComponentCounts Totals => new(_examined, _taxable, _gstFree, _unknown);

        public void Count(GstClassificationState resolved)
        {
            _examined++;
            switch (resolved.Classification)
            {
                case GstClassification.Taxable:
                    _taxable++;
                    break;
                case GstClassification.GstFree:
                    _gstFree++;
                    break;
                default:
                    _unknown++;
                    break;
            }
        }
    }
}
