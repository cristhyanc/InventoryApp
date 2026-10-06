using Inventory.Domain.Gst;
using Inventory.Domain.Purchases;
using Inventory.Domain.Reporting.Gst;
using Xunit;

namespace InventoryApi.Tests.Domain.Reporting.Gst;

/// <summary>
/// The period aggregation of purchase input GST (issue #432). Every per-component amount and
/// rounding assertion here must match <c>PurchaseGstPolicyTests</c>: this policy adds the
/// line/charge split and the period sum, and delegates the arithmetic to
/// <see cref="PurchaseGstPolicy"/> rather than restating it.
/// </summary>
public class PurchaseInputGstPolicyTests
{
    private static PurchaseGstLine Line(decimal quantity, decimal unitCost, GstClassification classification) =>
        new(quantity, unitCost, classification);

    private static PurchaseGstComponents Purchase(
        IReadOnlyList<PurchaseGstLine> lines,
        PurchaseGstCharge? delivery = null,
        PurchaseGstCharge? package = null) =>
        new(lines, delivery ?? new PurchaseGstCharge(null, GstClassification.Unknown),
            package ?? new PurchaseGstCharge(null, GstClassification.Unknown));

    [Fact]
    public void A_period_with_no_purchases_has_no_gst_and_nothing_unresolved()
    {
        var result = PurchaseInputGstPolicy.Calculate([]);

        Assert.Equal(0m, result.LineGst);
        Assert.Equal(0m, result.ChargeGst);
        Assert.Equal(0m, result.TotalGst);
        Assert.Equal(0, result.UnresolvedComponentCount);
        Assert.Equal(0m, result.UnresolvedAmount);
        Assert.True(result.IsComplete);
    }

    [Fact]
    public void Taxable_lines_and_charges_are_reported_separately_and_summed_into_the_total()
    {
        var result = PurchaseInputGstPolicy.Calculate(
        [
            Purchase(
                [Line(2m, 5.50m, GstClassification.Taxable)],
                delivery: new PurchaseGstCharge(11m, GstClassification.Taxable),
                package: new PurchaseGstCharge(2.20m, GstClassification.Taxable))
        ]);

        // 2 x 5.50 = 11.00 -> 1.00; delivery 11.00 -> 1.00; package 2.20 -> 0.20.
        Assert.Equal(1.00m, result.LineGst);
        Assert.Equal(1.20m, result.ChargeGst);
        Assert.Equal(2.20m, result.TotalGst);
        Assert.True(result.IsComplete);
    }

    [Fact]
    public void Gst_free_components_contribute_nothing_and_are_not_unresolved()
    {
        var result = PurchaseInputGstPolicy.Calculate(
        [
            Purchase(
                [Line(3m, 4m, GstClassification.GstFree)],
                delivery: new PurchaseGstCharge(11m, GstClassification.GstFree))
        ]);

        Assert.Equal(0m, result.TotalGst);
        Assert.Equal(0, result.UnresolvedComponentCount);
        Assert.Equal(0m, result.UnresolvedAmount);
        Assert.True(result.IsComplete);
    }

    [Fact]
    public void Unknown_components_are_counted_and_totalled_separately_without_contributing_gst()
    {
        var result = PurchaseInputGstPolicy.Calculate(
        [
            Purchase(
                [Line(2m, 5.50m, GstClassification.Taxable), Line(1m, 7.30m, GstClassification.Unknown)],
                delivery: new PurchaseGstCharge(9.90m, GstClassification.Unknown))
        ]);

        Assert.Equal(1.00m, result.LineGst);
        Assert.Equal(0m, result.ChargeGst);
        Assert.Equal(1.00m, result.TotalGst);
        Assert.Equal(2, result.UnresolvedComponentCount);
        Assert.Equal(17.20m, result.UnresolvedAmount);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void An_absent_or_zero_charge_is_never_unresolved()
    {
        var result = PurchaseInputGstPolicy.Calculate(
        [
            Purchase(
                [Line(1m, 10m, GstClassification.Taxable)],
                delivery: new PurchaseGstCharge(null, GstClassification.Unknown),
                package: new PurchaseGstCharge(0m, GstClassification.Unknown))
        ]);

        Assert.Equal(0, result.UnresolvedComponentCount);
        Assert.Equal(0m, result.UnresolvedAmount);
        Assert.True(result.IsComplete);
    }

    [Fact]
    public void Each_component_is_rounded_before_the_period_is_summed_not_after()
    {
        // Three identical taxable lines of 0.05 each: component rounding gives 0.00 three times,
        // while 0.15 / 11 would round to 0.01. The component-level rounding is authoritative.
        var result = PurchaseInputGstPolicy.Calculate(
        [
            Purchase([Line(1m, 0.05m, GstClassification.Taxable)]),
            Purchase([Line(1m, 0.05m, GstClassification.Taxable)]),
            Purchase([Line(1m, 0.05m, GstClassification.Taxable)])
        ]);

        Assert.Equal(0m, result.TotalGst);
    }

    [Fact]
    public void Mixed_purchases_across_a_period_accumulate_their_gst_and_their_unresolved_components()
    {
        var result = PurchaseInputGstPolicy.Calculate(
        [
            Purchase([Line(1m, 110m, GstClassification.Taxable)]),
            Purchase([Line(1m, 50m, GstClassification.GstFree)]),
            Purchase([Line(1m, 22m, GstClassification.Unknown)], package: new PurchaseGstCharge(5.50m, GstClassification.Taxable))
        ]);

        Assert.Equal(10m, result.LineGst);
        Assert.Equal(0.50m, result.ChargeGst);
        Assert.Equal(10.50m, result.TotalGst);
        Assert.Equal(1, result.UnresolvedComponentCount);
        Assert.Equal(22m, result.UnresolvedAmount);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void An_undefined_classification_is_refused_rather_than_counted_as_a_resolved_zero()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PurchaseInputGstPolicy.Calculate(
        [
            Purchase([Line(1m, 10m, (GstClassification)999)])
        ]));
    }
}
