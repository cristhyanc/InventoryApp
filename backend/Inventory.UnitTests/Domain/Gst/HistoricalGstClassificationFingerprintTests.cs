using Inventory.Domain.Gst;
using Xunit;

namespace InventoryApi.Tests.Domain.Gst;

/// <summary>
/// The stale- and foreign-preview guard of the historical GST classification workflow (issue #433).
///
/// Every assertion here is a case the Apply has to refuse, so each one is phrased as "this change
/// moves the fingerprint": a purchase amount, a classification, a provenance, a line, a whole
/// purchase, a product rule, a supplier default, and the owning business. The two stability
/// assertions are the other half of the guarantee - a fingerprint that moved for no reason would
/// make the workflow unusable rather than safe.
/// </summary>
public class HistoricalGstClassificationFingerprintTests
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;

    private static HistoricalGstPurchase Purchase(
        decimal? deliveryCost = 5.50m,
        GstClassificationState? delivery = null,
        decimal? packageCost = 3.00m,
        GstClassificationState? package = null,
        SupplierGstDefaults? supplierDefaults = null,
        IReadOnlyList<HistoricalGstPurchaseLine>? lines = null,
        int purchaseId = 7) =>
        new(
            purchaseId,
            deliveryCost,
            delivery ?? GstClassificationState.Unclassified,
            packageCost,
            package ?? GstClassificationState.Unclassified,
            supplierDefaults ?? SupplierGstDefaults.None,
            lines ?? [Line()]);

    private static HistoricalGstPurchaseLine Line(
        int lineId = 1,
        long productId = 100,
        decimal quantity = 10m,
        decimal unitCost = 1.21m,
        GstClassificationState? state = null,
        GstClassification productRule = GstRules.None) =>
        new(lineId, productId, quantity, unitCost, state ?? GstClassificationState.Unclassified, productRule);

    private static string Compute(int businessId, params HistoricalGstPurchase[] purchases) =>
        HistoricalGstClassificationFingerprint.Compute(businessId, purchases);

    [Fact]
    public void The_same_history_fingerprints_identically()
    {
        Assert.Equal(Compute(BusinessA, Purchase()), Compute(BusinessA, Purchase()));
    }

    /// <summary>
    /// A value re-read at a different scale is the same value, so <c>1.21</c> and <c>1.210000</c>
    /// must not look like an edit. Without this a preview would be rejected at random depending on
    /// how a decimal came back from the database.
    /// </summary>
    [Fact]
    public void A_decimal_of_a_different_scale_is_the_same_history()
    {
        Assert.Equal(
            Compute(BusinessA, Purchase(deliveryCost: 5.5m, lines: [Line(unitCost: 1.21m, quantity: 10m)])),
            Compute(BusinessA, Purchase(deliveryCost: 5.500m, lines: [Line(unitCost: 1.210000m, quantity: 10.00m)])));
    }

    /// <summary>
    /// The tenant binding. Two businesses whose histories render identically - two empty histories,
    /// most obviously - must not accept each other's previews.
    /// </summary>
    [Fact]
    public void Another_business_fingerprints_differently_even_with_identical_data()
    {
        Assert.NotEqual(Compute(BusinessA, Purchase()), Compute(BusinessB, Purchase()));
        Assert.NotEqual(Compute(BusinessA), Compute(BusinessB));
    }

    [Fact]
    public void A_changed_charge_amount_moves_the_fingerprint()
    {
        var baseline = Compute(BusinessA, Purchase());

        Assert.NotEqual(baseline, Compute(BusinessA, Purchase(deliveryCost: 5.51m)));
        Assert.NotEqual(baseline, Compute(BusinessA, Purchase(deliveryCost: null)));
        Assert.NotEqual(baseline, Compute(BusinessA, Purchase(packageCost: 0m)));
    }

    [Fact]
    public void A_changed_line_amount_moves_the_fingerprint()
    {
        var baseline = Compute(BusinessA, Purchase());

        Assert.NotEqual(baseline, Compute(BusinessA, Purchase(lines: [Line(quantity: 11m)])));
        Assert.NotEqual(baseline, Compute(BusinessA, Purchase(lines: [Line(unitCost: 1.22m)])));
        Assert.NotEqual(baseline, Compute(BusinessA, Purchase(lines: [Line(productId: 101)])));
    }

    /// <summary>
    /// A component classified by hand between the preview and the apply is exactly the race the
    /// guard exists for: the component is no longer eligible, so the plan the operator approved is
    /// no longer the plan that would be applied.
    /// </summary>
    [Fact]
    public void A_newly_classified_component_moves_the_fingerprint()
    {
        var baseline = Compute(BusinessA, Purchase());
        var manual = new GstClassificationState(GstClassification.Taxable, GstClassificationSource.Manual);

        Assert.NotEqual(baseline, Compute(BusinessA, Purchase(lines: [Line(state: manual)])));
        Assert.NotEqual(baseline, Compute(BusinessA, Purchase(delivery: manual)));
        Assert.NotEqual(baseline, Compute(BusinessA, Purchase(package: manual)));
    }

    /// <summary>
    /// Provenance alone moves it too: the same classification recorded as a person's choice rather
    /// than a rule's is a different auditable fact.
    /// </summary>
    [Fact]
    public void A_changed_provenance_alone_moves_the_fingerprint()
    {
        Assert.NotEqual(
            Compute(BusinessA, Purchase(lines:
            [
                Line(state: new GstClassificationState(
                    GstClassification.Taxable, GstClassificationSource.Manual)),
            ])),
            Compute(BusinessA, Purchase(lines:
            [
                Line(state: new GstClassificationState(
                    GstClassification.Taxable, GstClassificationSource.ProductRule)),
            ])));
    }

    [Fact]
    public void An_added_or_removed_line_moves_the_fingerprint()
    {
        var baseline = Compute(BusinessA, Purchase());

        Assert.NotEqual(baseline, Compute(BusinessA, Purchase(lines: [Line(), Line(lineId: 2, productId: 200)])));
        Assert.NotEqual(baseline, Compute(BusinessA, Purchase(lines: [])));
    }

    [Fact]
    public void An_added_or_removed_purchase_moves_the_fingerprint()
    {
        var baseline = Compute(BusinessA, Purchase());

        Assert.NotEqual(baseline, Compute(BusinessA, Purchase(), Purchase(purchaseId: 8)));
        Assert.NotEqual(baseline, Compute(BusinessA));
    }

    /// <summary>
    /// The rules are relevant source data, not just the purchases: a product rule or a supplier
    /// default configured between the preview and the apply would change what every eligible
    /// component becomes.
    /// </summary>
    [Fact]
    public void A_changed_product_rule_or_supplier_default_moves_the_fingerprint()
    {
        var baseline = Compute(BusinessA, Purchase());

        Assert.NotEqual(baseline, Compute(BusinessA, Purchase(lines: [Line(productRule: GstClassification.Taxable)])));
        Assert.NotEqual(
            baseline,
            Compute(BusinessA, Purchase(supplierDefaults: new SupplierGstDefaults(
                GstClassification.Taxable, GstRules.None, GstRules.None))));
        Assert.NotEqual(
            baseline,
            Compute(BusinessA, Purchase(supplierDefaults: new SupplierGstDefaults(
                GstRules.None, GstClassification.Taxable, GstRules.None))));
        Assert.NotEqual(
            baseline,
            Compute(BusinessA, Purchase(supplierDefaults: new SupplierGstDefaults(
                GstRules.None, GstRules.None, GstClassification.Taxable))));
    }

    /// <summary>
    /// The order rows come back in is not a change, so the rendering sorts by key. Without this the
    /// guard would reject previews whenever the database returned the same data differently ordered.
    /// </summary>
    [Fact]
    public void Row_order_is_not_a_change()
    {
        var first = Purchase(purchaseId: 7, lines: [Line(lineId: 1), Line(lineId: 2, productId: 200)]);
        var second = Purchase(purchaseId: 8, lines: [Line(lineId: 3, productId: 300)]);
        var reordered = Purchase(purchaseId: 7, lines: [Line(lineId: 2, productId: 200), Line(lineId: 1)]);

        Assert.Equal(Compute(BusinessA, first, second), Compute(BusinessA, second, reordered));
    }

    [Fact]
    public void The_fingerprint_is_lowercase_hexadecimal_sha256()
    {
        var fingerprint = Compute(BusinessA, Purchase());

        Assert.Equal(64, fingerprint.Length);
        Assert.All(fingerprint, character => Assert.Contains(character, "0123456789abcdef"));
    }
}
