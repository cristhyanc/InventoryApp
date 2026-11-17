using Inventory.Application.Costing;
using Inventory.Domain.FinancialConfiguration;
using Xunit;

namespace InventoryApi.Tests.Application.Costing;

/// <summary>
/// The one rule a forced backfill asks before it replaces a sale's stored historical cost (issue
/// #1261): <c>Inventory.Application.Costing.SaleCostPrecedence</c>.
///
/// It exists because <c>force</c> used to mean "overwrite", which let a repair tool replace a better
/// cost with a worse one - the Nayax backfill wrote the transaction-level Nayax cost (step 3) over a
/// sale costed from the inventory ledger (step 1), and the ledger backfill re-costed a
/// latest-purchase estimate (step 2) through a path that skips step 2 and so landed on step 3. A
/// forced backfill may now only improve a sale's cost, or correct a cost of the same provenance.
///
/// The same rule is asked again at the moment the cost is written, against the row rather than
/// against the snapshot the decision was made from, because the store takes its write lock after the
/// caller has decided: see the write-time region below, and
/// <c>SaleCostBackfillConcurrencyTests</c> for the two-writer regression over a real database.
/// </summary>
public class SaleCostPrecedenceTests
{
    private static readonly DateTime SaleAt = new(2026, 3, 2, 10, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(SaleCostOrigin.InventoryLedger, 1)]
    [InlineData(SaleCostOrigin.Estimated, 2)]
    [InlineData(SaleCostOrigin.NayaxTransactionExport, 3)]
    [InlineData(SaleCostOrigin.Unknown, 4)]
    public void Each_cost_source_has_its_precedence_step(SaleCostOrigin source, int step) =>
        Assert.Equal(step, SaleCostPrecedence.Step(source));

    /// <summary>
    /// A sale nothing has costed yet takes whatever a backfill decided, including a decision that
    /// leaves it uncosted but moves its status: that is a status and a provenance being recorded, not
    /// a cost being lost.
    /// </summary>
    [Theory]
    [InlineData(SaleCostStatus.Pending)]
    [InlineData(SaleCostStatus.Error)]
    public void An_uncosted_sale_is_filled_by_any_decision(SaleCostStatus status)
    {
        var sale = Sale(status, SaleCostOrigin.Unknown, unitCost: null);

        Assert.True(SaleCostPrecedence.Replaces(sale, Cost(1.20m, SaleCostOrigin.NayaxTransactionExport)));
        Assert.True(SaleCostPrecedence.Replaces(sale, Cost(1.50m, SaleCostOrigin.InventoryLedger)));
        Assert.True(SaleCostPrecedence.Replaces(
            sale,
            new SaleCostAssignment(
                null,
                status == SaleCostStatus.Pending ? SaleCostStatus.Error : SaleCostStatus.Pending,
                SaleCostOrigin.Unknown)));
    }

    /// <summary>
    /// And a decision that is exactly what the row already holds is not a replacement at all: writing
    /// it would record a change that did not happen and ask for a cost rebuild nothing needs.
    /// </summary>
    [Fact]
    public void A_decision_identical_to_the_stored_one_is_not_a_replacement()
    {
        Assert.False(SaleCostPrecedence.Replaces(
            Sale(SaleCostStatus.Pending, SaleCostOrigin.Unknown, unitCost: null),
            new SaleCostAssignment(null, SaleCostStatus.Pending, SaleCostOrigin.Unknown)));
        Assert.True(SaleCostPrecedence.Replaces(
            Sale(SaleCostStatus.Pending, SaleCostOrigin.Unknown, unitCost: null),
            new SaleCostAssignment(null, SaleCostStatus.Error, SaleCostOrigin.Unknown)));
    }

    /// <summary>
    /// A sale whose status says costed but which carries no cost value is not a costed sale: the
    /// unforced costing path already recosts exactly that row, and the precedence rule agrees.
    /// </summary>
    [Fact]
    public void A_costed_status_without_a_cost_value_is_still_uncosted()
    {
        var sale = Sale(SaleCostStatus.Costed, SaleCostOrigin.InventoryLedger, unitCost: null);

        Assert.True(SaleCostPrecedence.Replaces(sale, Cost(1.20m, SaleCostOrigin.NayaxTransactionExport)));
    }

    [Fact]
    public void A_better_source_replaces_a_worse_one()
    {
        Assert.True(SaleCostPrecedence.Replaces(
            Costed(0.90m, SaleCostOrigin.NayaxTransactionExport), Cost(1.50m, SaleCostOrigin.Estimated)));
        Assert.True(SaleCostPrecedence.Replaces(
            Costed(0.90m, SaleCostOrigin.NayaxTransactionExport), Cost(1.50m, SaleCostOrigin.InventoryLedger)));
        Assert.True(SaleCostPrecedence.Replaces(
            Costed(0.90m, SaleCostOrigin.Estimated), Cost(1.50m, SaleCostOrigin.InventoryLedger)));

        // A legacy cost with no recorded provenance is the worst of all, so any decided cost is an
        // improvement on it.
        Assert.True(SaleCostPrecedence.Replaces(
            Costed(0.90m, SaleCostOrigin.Unknown), Cost(1.20m, SaleCostOrigin.NayaxTransactionExport)));
    }

    [Fact]
    public void A_worse_source_never_replaces_a_better_one()
    {
        Assert.False(SaleCostPrecedence.Replaces(
            Costed(1.50m, SaleCostOrigin.InventoryLedger), Cost(1.20m, SaleCostOrigin.NayaxTransactionExport)));
        Assert.False(SaleCostPrecedence.Replaces(
            Costed(1.50m, SaleCostOrigin.InventoryLedger), Cost(1.40m, SaleCostOrigin.Estimated)));
        Assert.False(SaleCostPrecedence.Replaces(
            Costed(1.40m, SaleCostOrigin.Estimated), Cost(1.20m, SaleCostOrigin.NayaxTransactionExport)));
    }

    /// <summary>
    /// Uncosted is precedence step 4, so a decision that costs nothing never clears a cost a better
    /// step recorded - a forced backfill is a repair tool, not a way to erase cost history.
    /// </summary>
    [Fact]
    public void An_uncosted_decision_never_clears_a_stored_cost()
    {
        Assert.False(SaleCostPrecedence.Replaces(
            Costed(1.50m, SaleCostOrigin.InventoryLedger),
            new SaleCostAssignment(null, SaleCostStatus.Error, SaleCostOrigin.Unknown)));
        Assert.False(SaleCostPrecedence.Replaces(
            Costed(1.20m, SaleCostOrigin.NayaxTransactionExport),
            new SaleCostAssignment(null, SaleCostStatus.Pending, SaleCostOrigin.Unknown)));

        // Including a legacy cost of unknown provenance, which sits at the same step 4: a sale that
        // became unresolved keeps the number somebody recorded rather than losing it.
        Assert.False(SaleCostPrecedence.Replaces(
            Costed(0.90m, SaleCostOrigin.Unknown),
            new SaleCostAssignment(null, SaleCostStatus.Error, SaleCostOrigin.Unknown)));
    }

    /// <summary>
    /// Equal precedence is the one case where the value decides: a corrected Nayax cost, or a
    /// corrected ledger cost, is written; the same cost recomputed is not, so a repeated forced
    /// backfill stages nothing and enqueues no rebuild.
    /// </summary>
    [Theory]
    [InlineData(SaleCostOrigin.InventoryLedger)]
    [InlineData(SaleCostOrigin.Estimated)]
    [InlineData(SaleCostOrigin.NayaxTransactionExport)]
    public void An_equal_source_replaces_the_stored_cost_only_when_the_value_differs(SaleCostOrigin source)
    {
        Assert.False(SaleCostPrecedence.Replaces(Costed(1.20m, source), Cost(1.20m, source)));
        Assert.False(SaleCostPrecedence.Replaces(Costed(1.20m, source), Cost(1.200m, source)));
        Assert.True(SaleCostPrecedence.Replaces(Costed(1.20m, source), Cost(1.30m, source)));
    }

    /// <summary>
    /// A sale the legacy costing left as <c>LegacyEstimated</c> is a final cost like any other and is
    /// judged by its recorded provenance, not by its status.
    /// </summary>
    [Fact]
    public void A_legacy_estimated_sale_is_judged_by_its_recorded_source()
    {
        Assert.False(SaleCostPrecedence.Replaces(
            Sale(SaleCostStatus.LegacyEstimated, SaleCostOrigin.InventoryLedger, 1.50m),
            Cost(1.20m, SaleCostOrigin.NayaxTransactionExport)));
        Assert.True(SaleCostPrecedence.Replaces(
            Sale(SaleCostStatus.LegacyEstimated, SaleCostOrigin.Unknown, 1.50m),
            Cost(1.20m, SaleCostOrigin.NayaxTransactionExport)));
    }

    #region The same decision, asked again at write time

    /// <summary>
    /// Nothing was committed between the read the decision was made from and the write lock, so the
    /// decision is exactly the one its caller made.
    /// </summary>
    [Fact]
    public void An_unchanged_row_keeps_the_decision_the_caller_made()
    {
        var asRead = Stored(1.20m, SaleCostOrigin.NayaxTransactionExport);

        Assert.True(SaleCostPrecedence.StillReplaces(
            asRead, asRead, Cost(1.30m, SaleCostOrigin.NayaxTransactionExport)));
        Assert.True(SaleCostPrecedence.StillReplaces(
            asRead, asRead, Cost(1.50m, SaleCostOrigin.InventoryLedger)));
        Assert.False(SaleCostPrecedence.StillReplaces(
            asRead, asRead, Cost(1.20m, SaleCostOrigin.NayaxTransactionExport)));
    }

    /// <summary>
    /// The race the write-time question exists for: a better-sourced cost was committed on the sale
    /// after the decision was made, and the decision has lost the comparison it passed.
    /// </summary>
    [Fact]
    public void A_better_cost_committed_after_the_read_is_never_written_over()
    {
        Assert.False(SaleCostPrecedence.StillReplaces(
            Uncosted(),
            Stored(2m, SaleCostOrigin.InventoryLedger),
            Cost(1.20m, SaleCostOrigin.NayaxTransactionExport)));
        Assert.False(SaleCostPrecedence.StillReplaces(
            Stored(0.90m, SaleCostOrigin.Unknown),
            Stored(1.40m, SaleCostOrigin.Estimated),
            Cost(1.20m, SaleCostOrigin.NayaxTransactionExport)));
    }

    /// <summary>
    /// An equal-step decision is a correction of a value, and a correction computed from a history
    /// that has moved since stands down - whether the cost committed in the gap differs from it or is
    /// exactly it. The next run recomputes from the history that is actually stored, and meanwhile no
    /// cost rebuild is recorded for a sale this run changed nothing on.
    /// </summary>
    [Fact]
    public void An_equal_step_correction_stands_down_once_a_cost_was_committed()
    {
        // A different value: the committed correction is the one computed from the stored history.
        Assert.False(SaleCostPrecedence.StillReplaces(
            Uncosted(),
            Stored(2.50m, SaleCostOrigin.InventoryLedger),
            Cost(2m, SaleCostOrigin.InventoryLedger)));

        // And exactly the same value, which is the case that must record no rebuild.
        Assert.False(SaleCostPrecedence.StillReplaces(
            Uncosted(),
            Stored(2m, SaleCostOrigin.InventoryLedger),
            Cost(2m, SaleCostOrigin.InventoryLedger)));
    }

    /// <summary>
    /// But a strictly better step still improves the sale: the cost committed in the gap is a weaker
    /// claim than the decision, whatever history produced either of them.
    /// </summary>
    [Fact]
    public void A_better_step_still_replaces_a_cost_committed_after_the_read()
    {
        Assert.True(SaleCostPrecedence.StillReplaces(
            Uncosted(),
            Stored(1.10m, SaleCostOrigin.NayaxTransactionExport),
            Cost(2m, SaleCostOrigin.InventoryLedger)));
        Assert.True(SaleCostPrecedence.StillReplaces(
            Uncosted(),
            Stored(0.90m, SaleCostOrigin.Unknown),
            Cost(1.10m, SaleCostOrigin.NayaxTransactionExport)));
    }

    #endregion

    private static SaleCostAssignment Cost(decimal unitCost, SaleCostOrigin source) =>
        new(unitCost, SaleCostStatus.Costed, source);

    private static StoredSaleCost Stored(decimal unitCost, SaleCostOrigin source) =>
        new(unitCost, unitCost, SaleCostStatus.Costed, source);

    private static StoredSaleCost Uncosted() =>
        new(null, null, SaleCostStatus.Pending, SaleCostOrigin.Unknown);

    private static CostableSale Costed(decimal unitCost, SaleCostOrigin source) =>
        Sale(SaleCostStatus.Costed, source, unitCost);

    private static CostableSale Sale(SaleCostStatus status, SaleCostOrigin source, decimal? unitCost) =>
        new()
        {
            TransactionId = 1,
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
            ProductId = 10,
            AuthorizationTime = SaleAt,
            UnitCostAtSale = unitCost,
            CostOfGoodsSold = unitCost,
            CostingStatus = status,
            CostSource = source,
        };
}
