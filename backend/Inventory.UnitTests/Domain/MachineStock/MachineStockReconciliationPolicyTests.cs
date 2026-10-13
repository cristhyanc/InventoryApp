using Inventory.Domain.MachineStock;
using Xunit;

namespace InventoryApi.Tests.Domain.MachineStock;

/// <summary>
/// How a machine's reported stock becomes an observed quantity, and how it compares with the
/// ledger's expectation (issue #56). The recurring rule: an unknown on either side produces an
/// unknown discrepancy with the state that explains it, never a zero that would read as a balanced
/// machine or as shrinkage nobody has lost.
/// </summary>
public class MachineStockReconciliationPolicyTests
{
    private const long Product = 500;
    private static readonly DateTime Counted = new(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc);

    private static MachineStockProductLedger LedgerWithExpected(int expected) => new(
        Product, expected, MachineStockBaselineSource.CountedReconciliation, Counted, expected,
        RefilledQuantity: 0, ReturnedToStorageQuantity: 0, SoldQuantity: 0,
        UnexplainedChangeAtLastCount: null, Issues: []);

    [Fact]
    public void Observed_stock_is_the_sum_of_the_products_slots()
    {
        var observed = MachineStockObservationPolicy.ObservedQuantity(
            [new MachineStockObservationSlot(Product, 10, 4), new MachineStockObservationSlot(Product, 8, 0)]);

        Assert.Equal(14, observed);
    }

    [Fact]
    public void Observed_stock_is_unknown_when_a_slot_is_missing_a_value()
    {
        Assert.Null(MachineStockObservationPolicy.ObservedQuantity(
            [new MachineStockObservationSlot(Product, 10, null)]));
        Assert.Null(MachineStockObservationPolicy.ObservedQuantity(
            [new MachineStockObservationSlot(Product, null, 2)]));
    }

    [Fact]
    public void One_unusable_slot_makes_the_whole_product_unobserved()
    {
        Assert.Null(MachineStockObservationPolicy.ObservedQuantity(
        [
            new MachineStockObservationSlot(Product, 10, 4),
            new MachineStockObservationSlot(Product, 8, null),
        ]));
    }

    [Fact]
    public void Observed_stock_is_unknown_when_a_slot_is_not_a_plausible_quantity()
    {
        // More missing than the slot holds, and a negative missing count that would exceed PAR.
        Assert.Null(MachineStockObservationPolicy.ObservedQuantity(
            [new MachineStockObservationSlot(Product, 10, 11)]));
        Assert.Null(MachineStockObservationPolicy.ObservedQuantity(
            [new MachineStockObservationSlot(Product, 10, -1)]));
    }

    [Fact]
    public void A_product_with_no_slot_at_all_is_unobserved_rather_than_empty()
    {
        Assert.Null(MachineStockObservationPolicy.ObservedQuantity([]));
        Assert.Equal(0, MachineStockObservationPolicy.ObservedQuantity(
            [new MachineStockObservationSlot(Product, 10, 10)]));
    }

    [Fact]
    public void A_matching_observation_is_balanced()
    {
        var reconciliation = MachineStockReconciliationPolicy.Reconcile(LedgerWithExpected(12), 12);

        Assert.Equal(MachineStockReconciliationStatus.Balanced, reconciliation.Status);
        Assert.Equal(0, reconciliation.DiscrepancyQuantity);
        Assert.Equal(0, reconciliation.ShrinkageQuantity);
        Assert.Equal(0, reconciliation.OverageQuantity);
        Assert.Empty(reconciliation.Issues);
    }

    [Fact]
    public void Fewer_units_than_expected_is_a_shortage_and_is_reported_as_shrinkage()
    {
        var reconciliation = MachineStockReconciliationPolicy.Reconcile(LedgerWithExpected(12), 9);

        Assert.Equal(MachineStockReconciliationStatus.Shortage, reconciliation.Status);
        Assert.Equal(-3, reconciliation.DiscrepancyQuantity);
        Assert.Equal(3, reconciliation.ShrinkageQuantity);
        Assert.Equal(0, reconciliation.OverageQuantity);
    }

    [Fact]
    public void More_units_than_expected_is_an_overage()
    {
        var reconciliation = MachineStockReconciliationPolicy.Reconcile(LedgerWithExpected(12), 15);

        Assert.Equal(MachineStockReconciliationStatus.Overage, reconciliation.Status);
        Assert.Equal(3, reconciliation.DiscrepancyQuantity);
        Assert.Equal(0, reconciliation.ShrinkageQuantity);
        Assert.Equal(3, reconciliation.OverageQuantity);
    }

    [Fact]
    public void An_unobserved_product_keeps_its_expected_stock_and_reports_no_discrepancy()
    {
        var reconciliation = MachineStockReconciliationPolicy.Reconcile(LedgerWithExpected(12), observedQuantity: null);

        Assert.Equal(12, reconciliation.ExpectedQuantity);
        Assert.Null(reconciliation.ObservedQuantity);
        Assert.Null(reconciliation.DiscrepancyQuantity);
        Assert.Null(reconciliation.ShrinkageQuantity);
        Assert.Null(reconciliation.OverageQuantity);
        Assert.Equal(MachineStockReconciliationStatus.Unknown, reconciliation.Status);
        Assert.Contains(
            MachineStockDataQualityIssueCodes.ObservedStockUnavailable,
            reconciliation.Issues.Select(issue => issue.Code));
    }

    [Fact]
    public void An_unbaselined_product_reports_the_observation_without_inventing_a_discrepancy()
    {
        var reconciliation = MachineStockReconciliationPolicy.Reconcile(
            MachineStockLedger.NoRecordedHistory(Product), observedQuantity: 6);

        Assert.Null(reconciliation.ExpectedQuantity);
        Assert.Equal(6, reconciliation.ObservedQuantity);
        Assert.Null(reconciliation.DiscrepancyQuantity);
        Assert.Null(reconciliation.ShrinkageQuantity);
        Assert.Equal(MachineStockReconciliationStatus.Unknown, reconciliation.Status);
        Assert.Contains(
            MachineStockDataQualityIssueCodes.AwaitingOpeningCount,
            reconciliation.Issues.Select(issue => issue.Code));
    }

    [Fact]
    public void A_machine_reconciles_every_product_the_ledger_or_the_machine_knows()
    {
        var rows = MachineStockReconciliationPolicy.ReconcileMachine(
            [LedgerWithExpected(12) with { ProductId = 600 }, LedgerWithExpected(3) with { ProductId = 700 }],
            [new MachineStockObservationSlot(600, 20, 11), new MachineStockObservationSlot(800, 5, 0)]);

        Assert.Equal([600L, 700L, 800L], rows.Select(row => row.ProductId));

        var reconciled = rows[0];
        Assert.Equal(9, reconciled.ObservedQuantity);
        Assert.Equal(3, reconciled.ShrinkageQuantity);

        var unobserved = rows[1];
        Assert.Equal(3, unobserved.ExpectedQuantity);
        Assert.Null(unobserved.ObservedQuantity);
        Assert.Contains(
            MachineStockDataQualityIssueCodes.ObservedStockUnavailable,
            unobserved.Issues.Select(issue => issue.Code));

        var unknownToTheLedger = rows[2];
        Assert.Null(unknownToTheLedger.ExpectedQuantity);
        Assert.Equal(5, unknownToTheLedger.ObservedQuantity);
        Assert.Contains(
            MachineStockDataQualityIssueCodes.AwaitingOpeningCount,
            unknownToTheLedger.Issues.Select(issue => issue.Code));
    }
}
