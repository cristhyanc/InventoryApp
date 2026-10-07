using Inventory.Domain.Gst;
using Xunit;

namespace InventoryApi.Tests.Domain.Gst;

/// <summary>
/// The historical GST classification rule (issue #433): which stored components a configured rule
/// may classify, which rule wins, what stays unknown, and the input GST the plan makes available.
///
/// These are the financial and provenance decisions of the maintenance workflow, so they are pinned
/// here against plain values rather than through the API: every precedence case, the manual-wins
/// rule, the idempotent rerun, the absent-charge rule and the rounding reuse are decided by this
/// policy alone.
/// </summary>
public class HistoricalGstClassificationPolicyTests
{
    private static readonly GstClassificationState Unclassified = GstClassificationState.Unclassified;

    #region Eligibility

    [Fact]
    public void An_unclassified_component_is_reclassifiable()
    {
        Assert.True(HistoricalGstClassificationPolicy.IsReclassifiable(Unclassified));
    }

    [Theory]
    [InlineData(GstClassification.Taxable, GstClassificationSource.Manual)]
    [InlineData(GstClassification.GstFree, GstClassificationSource.Manual)]
    [InlineData(GstClassification.Taxable, GstClassificationSource.ProductRule)]
    [InlineData(GstClassification.GstFree, GstClassificationSource.SupplierDefault)]
    [InlineData(GstClassification.Taxable, GstClassificationSource.SupplierFeeDefault)]
    public void An_already_classified_component_is_never_reclassifiable(
        GstClassification classification, GstClassificationSource source)
    {
        Assert.False(HistoricalGstClassificationPolicy.IsReclassifiable(
            new GstClassificationState(classification, source)));
    }

    /// <summary>
    /// A manual decision wins even in the state nothing should ever produce - unclassified, but
    /// recorded as a person's choice. The rule is "manual is never overwritten", not "manual is
    /// never overwritten unless it looks empty".
    /// </summary>
    [Fact]
    public void A_manual_provenance_is_never_reclassifiable_even_when_it_carries_no_classification()
    {
        Assert.False(HistoricalGstClassificationPolicy.IsReclassifiable(
            new GstClassificationState(GstClassification.Unknown, GstClassificationSource.Manual)));
    }

    #endregion

    #region Precedence

    [Fact]
    public void A_product_rule_classifies_a_line_and_outranks_the_supplier_default()
    {
        var resolved = HistoricalGstClassificationPolicy.Resolve(
            GstComponentKind.ProductLine,
            GstClassification.Taxable,
            new SupplierGstDefaults(GstClassification.GstFree, GstClassification.GstFree, GstClassification.GstFree));

        Assert.Equal(
            new GstClassificationState(GstClassification.Taxable, GstClassificationSource.ProductRule), resolved);
    }

    [Fact]
    public void A_line_with_no_product_rule_falls_back_to_the_supplier_product_line_default()
    {
        var resolved = HistoricalGstClassificationPolicy.Resolve(
            GstComponentKind.ProductLine,
            GstRules.None,
            new SupplierGstDefaults(GstClassification.GstFree, GstClassification.Taxable, GstClassification.Taxable));

        Assert.Equal(
            new GstClassificationState(GstClassification.GstFree, GstClassificationSource.SupplierDefault), resolved);
    }

    [Theory]
    [InlineData(GstComponentKind.DeliveryCharge)]
    [InlineData(GstComponentKind.PackageCharge)]
    public void A_charge_is_classified_by_its_own_supplier_fee_default(GstComponentKind kind)
    {
        var defaults = new SupplierGstDefaults(
            ProductLines: GstClassification.GstFree,
            Delivery: GstClassification.Taxable,
            Package: GstClassification.GstFree);

        var resolved = HistoricalGstClassificationPolicy.Resolve(kind, GstRules.None, defaults);

        var expected = kind == GstComponentKind.DeliveryCharge
            ? GstClassification.Taxable
            : GstClassification.GstFree;
        Assert.Equal(new GstClassificationState(expected, GstClassificationSource.SupplierFeeDefault), resolved);
    }

    /// <summary>
    /// The charge carve-out, which exists because a supplier may sell GST-free goods and still
    /// charge GST on delivery: a charge reads neither the product rule nor the product-line default,
    /// so with no fee default of its own it stays unclassified however the lines are configured.
    /// </summary>
    [Theory]
    [InlineData(GstComponentKind.DeliveryCharge)]
    [InlineData(GstComponentKind.PackageCharge)]
    public void A_charge_never_inherits_the_product_rule_or_the_product_line_default(GstComponentKind kind)
    {
        var resolved = HistoricalGstClassificationPolicy.Resolve(
            kind,
            GstClassification.Taxable,
            new SupplierGstDefaults(GstClassification.Taxable, GstRules.None, GstRules.None));

        Assert.Equal(Unclassified, resolved);
    }

    [Fact]
    public void A_component_no_rule_covers_stays_unclassified()
    {
        Assert.Equal(
            Unclassified,
            HistoricalGstClassificationPolicy.Resolve(
                GstComponentKind.ProductLine, GstRules.None, SupplierGstDefaults.None));
    }

    /// <summary>
    /// "No rule" is <c>Unknown</c>, and an explicit GST-free rule is not: the two must stay
    /// distinguishable, because one classifies a component at $0 GST and the other leaves it
    /// visibly unresolved.
    /// </summary>
    [Fact]
    public void An_explicit_gst_free_rule_classifies_rather_than_leaving_the_component_unknown()
    {
        var resolved = HistoricalGstClassificationPolicy.Resolve(
            GstComponentKind.ProductLine, GstClassification.GstFree, SupplierGstDefaults.None);

        Assert.Equal(
            new GstClassificationState(GstClassification.GstFree, GstClassificationSource.ProductRule), resolved);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-1)]
    [InlineData(999)]
    public void An_undefined_rule_value_is_refused_rather_than_resolved(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HistoricalGstClassificationPolicy.Resolve(
            GstComponentKind.ProductLine, (GstClassification)value, SupplierGstDefaults.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => HistoricalGstClassificationPolicy.Resolve(
            GstComponentKind.DeliveryCharge,
            GstRules.None,
            new SupplierGstDefaults(GstRules.None, (GstClassification)value, GstRules.None)));
    }

    #endregion

    #region The plan

    [Fact]
    public void An_empty_history_plans_nothing()
    {
        var plan = HistoricalGstClassificationPolicy.Plan([]);

        Assert.Empty(plan.Changes);
        Assert.Equal(0, plan.Summary.ComponentsExamined);
        Assert.Equal(0m, plan.Summary.InputGst);
        Assert.False(plan.Summary.ClassifiesAnything);
    }

    /// <summary>
    /// The worked example the preview reports: three lines and both charges, one of each outcome,
    /// with the GST that results. $12.10 taxable is $1.10 of input GST and $5.50 is $0.50; the
    /// GST-free line and the uncovered line and charge contribute none.
    /// </summary>
    [Fact]
    public void A_mixed_history_reports_each_outcome_its_charge_counts_and_the_resulting_gst()
    {
        var plan = HistoricalGstClassificationPolicy.Plan(
        [
            new HistoricalGstPurchase(
                PurchaseId: 7,
                DeliveryCost: 5.50m,
                Delivery: Unclassified,
                PackageCost: 3.00m,
                Package: Unclassified,
                SupplierDefaults: new SupplierGstDefaults(
                    ProductLines: GstClassification.GstFree,
                    Delivery: GstClassification.Taxable,
                    Package: GstRules.None),
                Lines:
                [
                    // Taxable by its own product rule: 10 x $1.21 = $12.10 inclusive.
                    new HistoricalGstPurchaseLine(1, 100, 10m, 1.21m, Unclassified, GstClassification.Taxable),
                    // GST-free by the supplier's product-line default.
                    new HistoricalGstPurchaseLine(2, 200, 4m, 2.00m, Unclassified, GstRules.None),
                    // Already classified by hand: not examined at all.
                    new HistoricalGstPurchaseLine(
                        3,
                        300,
                        1m,
                        9.90m,
                        new GstClassificationState(GstClassification.Taxable, GstClassificationSource.Manual),
                        GstClassification.GstFree),
                ]),
            new HistoricalGstPurchase(
                PurchaseId: 8,
                DeliveryCost: 4.00m,
                Delivery: Unclassified,
                PackageCost: null,
                Package: Unclassified,
                SupplierDefaults: SupplierGstDefaults.None,
                Lines: [new HistoricalGstPurchaseLine(4, 100, 1m, 7.00m, Unclassified, GstRules.None)]),
        ]);

        var summary = plan.Summary;
        Assert.Equal(new HistoricalGstComponentCounts(3, 1, 1, 1), summary.ProductLines);
        Assert.Equal(new HistoricalGstComponentCounts(2, 1, 0, 1), summary.DeliveryCharges);
        // Purchase 7's package charge exists but has no fee default; purchase 8 has no package charge.
        Assert.Equal(new HistoricalGstComponentCounts(1, 0, 0, 1), summary.PackageCharges);
        Assert.Equal(6, summary.ComponentsExamined);
        Assert.Equal(2, summary.BecomingTaxable);
        Assert.Equal(1, summary.BecomingGstFree);
        Assert.Equal(3, summary.StayingUnknown);
        Assert.Equal(2, summary.PurchasesExamined);
        Assert.Equal(1.10m, summary.LineGst);
        Assert.Equal(0.50m, summary.ChargeGst);
        Assert.Equal(1.60m, summary.InputGst);
        // $7.00 line + $3.00 package + $4.00 delivery left unresolved.
        Assert.Equal(14.00m, summary.StayingUnknownAmount);
        Assert.True(summary.ClassifiesAnything);
    }

    /// <summary>
    /// A plan writes one change per classified component, naming the component and its provenance -
    /// and nothing for a component that stays unknown, because there is nothing to record.
    /// </summary>
    [Fact]
    public void The_plan_writes_one_change_per_classified_component_with_its_provenance()
    {
        var plan = HistoricalGstClassificationPolicy.Plan(
        [
            new HistoricalGstPurchase(
                PurchaseId: 7,
                DeliveryCost: 5.50m,
                Delivery: Unclassified,
                PackageCost: 3.00m,
                Package: Unclassified,
                SupplierDefaults: new SupplierGstDefaults(
                    GstClassification.GstFree, GstClassification.Taxable, GstRules.None),
                Lines:
                [
                    new HistoricalGstPurchaseLine(1, 100, 1m, 1.10m, Unclassified, GstClassification.Taxable),
                    new HistoricalGstPurchaseLine(2, 200, 1m, 2.00m, Unclassified, GstRules.None),
                ]),
        ]);

        Assert.Equal(
            [
                new HistoricalGstClassificationChange(
                    GstComponentKind.ProductLine, 7, 1, GstClassification.Taxable,
                    GstClassificationSource.ProductRule),
                new HistoricalGstClassificationChange(
                    GstComponentKind.ProductLine, 7, 2, GstClassification.GstFree,
                    GstClassificationSource.SupplierDefault),
                new HistoricalGstClassificationChange(
                    GstComponentKind.DeliveryCharge, 7, null, GstClassification.Taxable,
                    GstClassificationSource.SupplierFeeDefault),
            ],
            plan.Changes);
    }

    /// <summary>
    /// Re-running the plan over the state an Apply leaves behind examines nothing and changes
    /// nothing: the acceptance criterion's idempotent rerun, decided by the policy rather than by
    /// the database.
    /// </summary>
    [Fact]
    public void Re_planning_an_applied_history_examines_nothing()
    {
        var applied = new HistoricalGstPurchase(
            PurchaseId: 7,
            DeliveryCost: 5.50m,
            Delivery: new GstClassificationState(
                GstClassification.Taxable, GstClassificationSource.SupplierFeeDefault),
            PackageCost: 3.00m,
            Package: new GstClassificationState(
                GstClassification.GstFree, GstClassificationSource.SupplierFeeDefault),
            SupplierDefaults: new SupplierGstDefaults(
                GstClassification.Taxable, GstClassification.Taxable, GstClassification.GstFree),
            Lines:
            [
                new HistoricalGstPurchaseLine(
                    1,
                    100,
                    1m,
                    1.10m,
                    new GstClassificationState(GstClassification.Taxable, GstClassificationSource.ProductRule),
                    GstClassification.Taxable),
            ]);

        var plan = HistoricalGstClassificationPolicy.Plan([applied]);

        Assert.Empty(plan.Changes);
        Assert.Equal(HistoricalGstClassificationSummary.Empty, plan.Summary);
    }

    /// <summary>
    /// Decision D3, over the maintenance workflow: a null or zero charge does not exist, so it is
    /// neither classified nor counted as staying unknown, whatever the supplier configured.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void An_absent_or_zero_charge_is_not_examined(int? amount)
    {
        var plan = HistoricalGstClassificationPolicy.Plan(
        [
            new HistoricalGstPurchase(
                PurchaseId: 7,
                DeliveryCost: amount,
                Delivery: Unclassified,
                PackageCost: amount,
                Package: Unclassified,
                SupplierDefaults: new SupplierGstDefaults(
                    GstRules.None, GstClassification.Taxable, GstClassification.Taxable),
                Lines: []),
        ]);

        Assert.Empty(plan.Changes);
        Assert.Equal(0, plan.Summary.ComponentsExamined);
        Assert.Equal(0, plan.Summary.PurchasesExamined);
    }

    /// <summary>
    /// The rounding reuse: each component's GST is rounded on its own amount and the already-rounded
    /// amounts are summed. Two $0.05 taxable lines are $0.00 each by <c>round(0.05 / 11, 2)</c>, so
    /// the plan's GST is $0.00 - not the $0.01 that dividing their $0.10 total by 11 would give.
    /// </summary>
    [Fact]
    public void Gst_is_summed_from_individually_rounded_components()
    {
        var plan = HistoricalGstClassificationPolicy.Plan(
        [
            new HistoricalGstPurchase(
                PurchaseId: 7,
                DeliveryCost: null,
                Delivery: Unclassified,
                PackageCost: null,
                Package: Unclassified,
                SupplierDefaults: SupplierGstDefaults.None,
                Lines:
                [
                    new HistoricalGstPurchaseLine(1, 100, 1m, 0.05m, Unclassified, GstClassification.Taxable),
                    new HistoricalGstPurchaseLine(2, 200, 1m, 0.05m, Unclassified, GstClassification.Taxable),
                ]),
        ]);

        Assert.Equal(0m, plan.Summary.LineGst);
        Assert.Equal(2, plan.Summary.BecomingTaxable);
    }

    /// <summary>
    /// A line's inclusive amount is <c>round(Quantity * UnitCost, 2)</c> before any GST is derived
    /// from it: 3 x $1.005 rounds to $3.02 away from zero, whose GST is $0.27.
    /// </summary>
    [Fact]
    public void A_lines_amount_is_rounded_before_its_gst_is_derived()
    {
        var plan = HistoricalGstClassificationPolicy.Plan(
        [
            new HistoricalGstPurchase(
                PurchaseId: 7,
                DeliveryCost: null,
                Delivery: Unclassified,
                PackageCost: null,
                Package: Unclassified,
                SupplierDefaults: SupplierGstDefaults.None,
                Lines: [new HistoricalGstPurchaseLine(1, 100, 3m, 1.005m, Unclassified, GstClassification.Taxable)]),
        ]);

        Assert.Equal(0.27m, plan.Summary.LineGst);
    }

    /// <summary>
    /// A purchase with no supplier has no configured default, which is not a GST-free default: its
    /// unruled lines and its charges all stay unknown.
    /// </summary>
    [Fact]
    public void A_purchase_with_no_supplier_defaults_classifies_only_from_product_rules()
    {
        var plan = HistoricalGstClassificationPolicy.Plan(
        [
            new HistoricalGstPurchase(
                PurchaseId: 7,
                DeliveryCost: 5.50m,
                Delivery: Unclassified,
                PackageCost: null,
                Package: Unclassified,
                SupplierDefaults: SupplierGstDefaults.None,
                Lines:
                [
                    new HistoricalGstPurchaseLine(1, 100, 1m, 11.00m, Unclassified, GstClassification.Taxable),
                    new HistoricalGstPurchaseLine(2, 200, 1m, 4.00m, Unclassified, GstRules.None),
                ]),
        ]);

        Assert.Equal(1.00m, plan.Summary.LineGst);
        Assert.Equal(0m, plan.Summary.ChargeGst);
        Assert.Equal(2, plan.Summary.StayingUnknown);
        Assert.Equal(9.50m, plan.Summary.StayingUnknownAmount);
        Assert.Single(plan.Changes);
    }

    #endregion
}
