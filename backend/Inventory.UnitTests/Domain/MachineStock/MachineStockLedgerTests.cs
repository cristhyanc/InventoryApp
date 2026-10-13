using Inventory.Domain.MachineStock;
using Xunit;

namespace InventoryApi.Tests.Domain.MachineStock;

/// <summary>
/// The machine-stock ledger projection (issue #56). The two rules under test throughout are that
/// expected stock is never invented - without a baseline it stays unknown rather than becoming the
/// net of the movements - and that a count supersedes rather than corrects: it sets the level from
/// its own instant, reports the difference it absorbed, and leaves the history before it alone.
/// </summary>
public class MachineStockLedgerTests
{
    private const long Product = 500;
    private static readonly DateTime Day1 = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime At(int day, int hour = 0) =>
        Day1.AddDays(day - 1).AddHours(hour);

    private static MachineStockProductLedger ProjectOne(
        IReadOnlyCollection<MachineRefillFact>? refills = null,
        IReadOnlyCollection<MachineSaleFact>? sales = null,
        IReadOnlyCollection<MachineStockCountFact>? counts = null,
        IReadOnlyCollection<MachineStockOpeningBaselineFact>? baselines = null) =>
        Assert.Single(MachineStockLedger.Project(new MachineStockLedgerInputs(
            refills ?? [], sales ?? [], counts ?? [], baselines ?? [])));

    [Fact]
    public void Without_a_baseline_expected_stock_is_unknown_and_the_movements_are_still_reported()
    {
        var ledger = ProjectOne(
            refills: [new MachineRefillFact(1, Product, -10, At(1))],
            sales: [new MachineSaleFact(9001, Product, At(2)), new MachineSaleFact(9002, Product, At(3))]);

        Assert.Null(ledger.ExpectedQuantity);
        Assert.Equal(MachineStockBaselineSource.None, ledger.BaselineSource);
        Assert.Null(ledger.BaselineAtUtc);
        Assert.Equal(10, ledger.RefilledQuantity);
        Assert.Equal(2, ledger.SoldQuantity);
        Assert.Contains(
            MachineStockDataQualityIssueCodes.AwaitingOpeningCount,
            ledger.Issues.Select(issue => issue.Code));
    }

    [Fact]
    public void A_count_becomes_the_baseline_and_later_refills_and_sales_move_it()
    {
        var ledger = ProjectOne(
            refills: [new MachineRefillFact(1, Product, -8, At(3))],
            sales: [new MachineSaleFact(9001, Product, At(4)), new MachineSaleFact(9002, Product, At(5))],
            counts: [new MachineStockCountFact(1, Product, 20, At(2))]);

        Assert.Equal(26, ledger.ExpectedQuantity);
        Assert.Equal(MachineStockBaselineSource.CountedReconciliation, ledger.BaselineSource);
        Assert.Equal(At(2), ledger.BaselineAtUtc);
        Assert.Equal(20, ledger.BaselineQuantity);
        Assert.Equal(8, ledger.RefilledQuantity);
        Assert.Equal(2, ledger.SoldQuantity);
        Assert.Null(ledger.UnexplainedChangeAtLastCount);
        Assert.Empty(ledger.Issues);
    }

    [Fact]
    public void A_return_to_storage_reduces_expected_machine_stock()
    {
        var ledger = ProjectOne(
            refills: [new MachineRefillFact(2, Product, 5, At(3))],
            counts: [new MachineStockCountFact(1, Product, 20, At(2))]);

        Assert.Equal(15, ledger.ExpectedQuantity);
        Assert.Equal(5, ledger.ReturnedToStorageQuantity);
        Assert.Equal(0, ledger.RefilledQuantity);
    }

    [Fact]
    public void Movements_before_the_count_do_not_move_the_counted_level()
    {
        var ledger = ProjectOne(
            refills: [new MachineRefillFact(1, Product, -40, At(1))],
            sales: [new MachineSaleFact(9001, Product, At(1, 12))],
            counts: [new MachineStockCountFact(1, Product, 6, At(2))]);

        Assert.Equal(6, ledger.ExpectedQuantity);
        Assert.Equal(0, ledger.RefilledQuantity);
        Assert.Equal(0, ledger.SoldQuantity);
    }

    [Fact]
    public void A_second_count_reports_the_difference_it_absorbed()
    {
        var ledger = ProjectOne(
            refills: [new MachineRefillFact(1, Product, -10, At(3))],
            sales: [new MachineSaleFact(9001, Product, At(4))],
            counts:
            [
                new MachineStockCountFact(1, Product, 20, At(2)),
                // Expected at this point is 20 + 10 - 1 = 29, but 25 are actually there.
                new MachineStockCountFact(2, Product, 25, At(5)),
            ]);

        Assert.Equal(25, ledger.ExpectedQuantity);
        Assert.Equal(4, ledger.UnexplainedChangeAtLastCount);
        Assert.Equal(At(5), ledger.BaselineAtUtc);
        Assert.Equal(0, ledger.RefilledQuantity);
        Assert.Equal(0, ledger.SoldQuantity);
    }

    [Fact]
    public void A_first_count_absorbs_nothing_because_nothing_was_expected()
    {
        var ledger = ProjectOne(
            refills: [new MachineRefillFact(1, Product, -10, At(1))],
            counts: [new MachineStockCountFact(1, Product, 3, At(2))]);

        Assert.Null(ledger.UnexplainedChangeAtLastCount);
        Assert.Equal(3, ledger.ExpectedQuantity);
    }

    [Fact]
    public void A_count_at_the_same_instant_as_a_refill_is_the_authority_on_that_instant()
    {
        var ledger = ProjectOne(
            refills: [new MachineRefillFact(1, Product, -10, At(2))],
            counts: [new MachineStockCountFact(1, Product, 4, At(2))]);

        Assert.Equal(4, ledger.ExpectedQuantity);
        Assert.Equal(0, ledger.RefilledQuantity);
    }

    [Fact]
    public void The_cost_transition_baseline_opens_the_ledger_when_no_count_exists()
    {
        var ledger = ProjectOne(
            refills: [new MachineRefillFact(1, Product, -5, At(3))],
            sales: [new MachineSaleFact(9001, Product, At(4))],
            baselines: [new MachineStockOpeningBaselineFact(Product, 12, At(2))]);

        Assert.Equal(16, ledger.ExpectedQuantity);
        Assert.Equal(MachineStockBaselineSource.CostTransitionBaseline, ledger.BaselineSource);
        Assert.Equal(At(2), ledger.BaselineAtUtc);
        Assert.Equal(12, ledger.BaselineQuantity);
        Assert.Empty(ledger.Issues);
    }

    [Fact]
    public void Movements_at_or_before_the_transition_cutoff_are_already_in_the_baseline()
    {
        var ledger = ProjectOne(
            refills: [new MachineRefillFact(1, Product, -5, At(2))],
            sales: [new MachineSaleFact(9001, Product, At(1))],
            baselines: [new MachineStockOpeningBaselineFact(Product, 12, At(2))]);

        Assert.Equal(12, ledger.ExpectedQuantity);
        Assert.Equal(0, ledger.RefilledQuantity);
        Assert.Equal(0, ledger.SoldQuantity);
    }

    [Fact]
    public void A_count_overrides_an_earlier_transition_baseline()
    {
        var ledger = ProjectOne(
            counts: [new MachineStockCountFact(1, Product, 2, At(5))],
            baselines: [new MachineStockOpeningBaselineFact(Product, 12, At(2))]);

        Assert.Equal(2, ledger.ExpectedQuantity);
        Assert.Equal(MachineStockBaselineSource.CountedReconciliation, ledger.BaselineSource);
        // Ten of the twelve units the transition recorded are not in the machine.
        Assert.Equal(10, ledger.UnexplainedChangeAtLastCount);
    }

    [Fact]
    public void The_latest_transition_baseline_wins_when_more_than_one_exists()
    {
        var ledger = ProjectOne(
            baselines:
            [
                new MachineStockOpeningBaselineFact(Product, 12, At(2)),
                new MachineStockOpeningBaselineFact(Product, 30, At(6)),
            ]);

        Assert.Equal(30, ledger.ExpectedQuantity);
        Assert.Equal(At(6), ledger.BaselineAtUtc);
    }

    [Fact]
    public void Expected_stock_that_goes_negative_is_reported_rather_than_clamped()
    {
        var ledger = ProjectOne(
            sales: [new MachineSaleFact(9001, Product, At(3)), new MachineSaleFact(9002, Product, At(4))],
            counts: [new MachineStockCountFact(1, Product, 1, At(2))]);

        Assert.Equal(-1, ledger.ExpectedQuantity);
        Assert.Contains(
            MachineStockDataQualityIssueCodes.ExpectedStockNegative,
            ledger.Issues.Select(issue => issue.Code));
    }

    [Fact]
    public void A_later_count_clears_a_negative_position_rather_than_leaving_the_issue_standing()
    {
        var ledger = ProjectOne(
            sales: [new MachineSaleFact(9001, Product, At(3))],
            counts:
            [
                new MachineStockCountFact(1, Product, 0, At(2)),
                new MachineStockCountFact(2, Product, 7, At(4)),
            ]);

        Assert.Equal(7, ledger.ExpectedQuantity);
        Assert.DoesNotContain(
            MachineStockDataQualityIssueCodes.ExpectedStockNegative,
            ledger.Issues.Select(issue => issue.Code));
        // The ledger expected -1 and the operator counted 7: eight units more than it could explain,
        // reported as a negative unexplained change rather than hidden by the count.
        Assert.Equal(-8, ledger.UnexplainedChangeAtLastCount);
    }

    [Fact]
    public void Each_product_is_projected_separately_and_ordered_by_id()
    {
        var ledger = MachineStockLedger.Project(new MachineStockLedgerInputs(
            [new MachineRefillFact(1, 700, -4, At(3)), new MachineRefillFact(2, 600, -6, At(3))],
            [new MachineSaleFact(9001, 600, At(4))],
            [new MachineStockCountFact(1, 600, 10, At(2)), new MachineStockCountFact(2, 700, 1, At(2))],
            []));

        Assert.Equal([600L, 700L], ledger.Select(product => product.ProductId));
        Assert.Equal(15, ledger[0].ExpectedQuantity);
        Assert.Equal(5, ledger[1].ExpectedQuantity);
    }

    [Fact]
    public void A_product_with_only_a_baseline_and_no_movement_is_still_projected()
    {
        var ledger = ProjectOne(baselines: [new MachineStockOpeningBaselineFact(Product, 9, At(2))]);

        Assert.Equal(9, ledger.ExpectedQuantity);
        Assert.Equal(0, ledger.SoldQuantity);
    }

    [Fact]
    public void A_product_the_ledger_has_never_seen_is_awaiting_an_opening_count()
    {
        var ledger = MachineStockLedger.NoRecordedHistory(Product);

        Assert.Null(ledger.ExpectedQuantity);
        Assert.Equal(MachineStockBaselineSource.None, ledger.BaselineSource);
        Assert.Equal(0, ledger.RefilledQuantity);
        Assert.Equal(0, ledger.ReturnedToStorageQuantity);
        Assert.Equal(0, ledger.SoldQuantity);
        Assert.Contains(
            MachineStockDataQualityIssueCodes.AwaitingOpeningCount,
            ledger.Issues.Select(issue => issue.Code));
    }
}
