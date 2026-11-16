using Inventory.Application;
using Inventory.Application.Costing;
using Inventory.Application.Nayax;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.NayaxSqs;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.Costing;

/// <summary>
/// The Application sale-costing use cases (issue #297) over an in-memory
/// <see cref="ISaleCostingStore"/>: historical cost precedence and provenance, forced and unforced
/// re-costing, dry-run versus apply, idempotency on repeat runs, unmatched products, and the
/// historical Nayax-cost backfill's independence from the live Nayax API.
/// </summary>
public class SaleCostingUseCaseTests
{
    private static readonly DateTime SaleAt = new(2026, 3, 2, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Unforced_costing_keeps_an_already_costed_sale_unchanged()
    {
        var store = new FakeStore();
        var rebuild = LedgerCost(10, 1.50m);
        var sale = Sale(1, productId: 10, nayaxCost: 1.20m, unitCost: 0.90m, status: SaleCostStatus.Costed);

        var cost = await new CostSale(store, rebuild.Object).Handle(sale);

        Assert.Null(cost);
        rebuild.Verify(x => x.GetAverageUnitCostAtAsync(
            It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Forced_costing_recosts_an_already_costed_sale_from_the_inventory_ledger()
    {
        var store = new FakeStore();
        var sale = Sale(1, productId: 10, nayaxCost: 1.20m, unitCost: 0.90m, status: SaleCostStatus.Costed);

        var cost = await new CostSale(store, LedgerCost(10, 1.50m).Object).Handle(sale, force: true);

        Assert.Equal(new SaleCostAssignment(1.50m, SaleCostStatus.Costed, SaleCostOrigin.InventoryLedger), cost);
    }

    [Fact]
    public async Task A_costed_sale_missing_its_cost_values_is_recosted_without_force()
    {
        var store = new FakeStore();
        var sale = Sale(1, productId: 10, nayaxCost: 1.20m, unitCost: null, status: SaleCostStatus.Costed);

        var cost = await new CostSale(store, LedgerCost(10, null).Object).Handle(sale);

        Assert.Equal(new SaleCostAssignment(1.20m, SaleCostStatus.Costed, SaleCostOrigin.NayaxTransactionExport), cost);
    }

    [Fact]
    public async Task An_unresolved_sale_without_a_Nayax_cost_is_an_error_and_never_reads_the_ledger()
    {
        var store = new FakeStore();
        var rebuild = LedgerCost(10, 1.50m);
        var sale = Sale(1, productId: null, nayaxCost: null, productName: "Unknown drink");

        var cost = await new CostSale(store, rebuild.Object).Handle(sale);

        Assert.Equal(new SaleCostAssignment(null, SaleCostStatus.Error, SaleCostOrigin.Unknown), cost);
        rebuild.Verify(x => x.GetAverageUnitCostAtAsync(
            It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task An_unresolved_sale_with_a_Nayax_cost_is_costed_from_the_Nayax_export()
    {
        var store = new FakeStore();
        var sale = Sale(1, productId: null, nayaxCost: 1.10m, productName: "Unknown drink");

        var cost = await new CostSale(store, LedgerCost(10, 1.50m).Object).Handle(sale);

        Assert.Equal(new SaleCostAssignment(1.10m, SaleCostStatus.Costed, SaleCostOrigin.NayaxTransactionExport), cost);
    }

    /// <summary>
    /// Issue #758: the association is decided once by the writer that stored the sale. A sale that
    /// writer left unresolved is not rescued at costing time by a name that would match exactly one
    /// product today - that is what stops a rename or a duplicate name moving historical COGS.
    /// </summary>
    [Fact]
    public async Task An_unresolved_sale_is_never_costed_from_a_product_its_name_would_match()
    {
        var store = new FakeStore();
        var rebuild = LedgerCost(10, 1.50m);
        var sale = Sale(1, productId: null, nayaxCost: null, productName: "Snack (3.00)");

        var cost = await new CostSale(store, rebuild.Object).Handle(sale);

        Assert.Equal(new SaleCostAssignment(null, SaleCostStatus.Error, SaleCostOrigin.Unknown), cost);
        rebuild.Verify(x => x.GetAverageUnitCostAtAsync(
            It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// The sale-time path still goes straight from step 1 to step 3 (<c>AGENTS.md</c> § Inventory and
    /// historical costing invariants): the inventory-cost rebuild that follows a stored sale replays
    /// step 2 straight afterwards, so asking for the latest costed purchase here would be a second
    /// answer to a question the rebuild already answers. Only a forced backfill, which has no such
    /// rebuild inside its transaction, asks for the complete precedence (issue #1261).
    /// </summary>
    [Fact]
    public async Task The_sale_time_path_does_not_apply_the_latest_purchase_step()
    {
        var store = new FakeStore();
        var rebuild = LedgerCost(10, null, latestPurchase: 1.60m);
        var sale = Sale(1, productId: 10, nayaxCost: 1.10m);

        var cost = await new CostSale(store, rebuild.Object).Handle(sale);

        Assert.Equal(new SaleCostAssignment(1.10m, SaleCostStatus.Costed, SaleCostOrigin.NayaxTransactionExport), cost);
        rebuild.Verify(x => x.GetLatestPurchaseUnitCostBeforeAsync(
            It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// And at or before the transition cutoff neither ledger step applies: the baseline is the
    /// opening and the replay ignores everything there, so a forced backfill reads no purchase
    /// before the sale either and falls to the sale's own Nayax cost.
    /// </summary>
    [Fact]
    public async Task A_sale_at_or_before_the_transition_cutoff_reads_neither_ledger_step()
    {
        var store = new FakeStore { Cutoffs = { [10] = SaleAt } };
        var rebuild = LedgerCost(10, 1.50m, latestPurchase: 1.60m);
        var sale = Sale(1, productId: 10, nayaxCost: 1.10m);

        var cost = await new CostSale(store, rebuild.Object).Handle(
            sale, force: true, SaleCostPrecedenceScope.Complete);

        Assert.Equal(new SaleCostAssignment(1.10m, SaleCostStatus.Costed, SaleCostOrigin.NayaxTransactionExport), cost);
        rebuild.Verify(x => x.GetLatestPurchaseUnitCostBeforeAsync(
            It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_sale_at_or_before_the_transition_cutoff_is_not_costed_from_the_ledger()
    {
        var store = new FakeStore { Cutoffs = { [10] = SaleAt } };
        var sale = Sale(1, productId: 10, nayaxCost: null);

        var cost = await new CostSale(store, LedgerCost(10, 1.50m).Object).Handle(sale);

        Assert.Equal(new SaleCostAssignment(null, SaleCostStatus.Pending, SaleCostOrigin.Unknown), cost);
    }

    /// <summary>
    /// A sale that consumed no unit has no historical cost, even when forced: there is nothing to
    /// cost. Which sales those are is <c>NayaxSaleCountingPolicy.ConsumesStock</c>, so this covers
    /// every status that dispensed nothing - pending, cancelled, declined, unrecognised and none at
    /// all.
    /// </summary>
    [Theory]
    [InlineData(NayaxTransactionStatusIds.PendingSettlementNotFinal)]
    [InlineData(NayaxTransactionStatusIds.PendingBatch)]
    [InlineData(NayaxTransactionStatusIds.CancelledOrDeclined26)]
    [InlineData(NayaxTransactionStatusIds.CancelledOrDeclined250)]
    [InlineData(21)]
    [InlineData(null)]
    public async Task A_sale_that_consumed_no_unit_is_left_uncosted_and_pending_even_when_forced(
        int? transactionStatusId)
    {
        var store = new FakeStore();
        var sale = Sale(1, productId: 10, nayaxCost: 1.10m, unitCost: 1m, status: SaleCostStatus.Costed,
            transactionStatusId: transactionStatusId);

        var cost = await new CostSale(store, LedgerCost(10, 1.50m).Object).Handle(sale, force: true);

        Assert.Equal(new SaleCostAssignment(null, SaleCostStatus.Pending, SaleCostOrigin.Unknown), cost);
    }

    /// <summary>
    /// A <strong>refunded</strong> sale is costed, and that is issue #1107's split: its money is
    /// reversed but the machine dispensed its unit, so it carries the historical cost a completed
    /// sale at the same instant would - from the inventory ledger, by the same precedence. Before the
    /// split it was left uncosted and pending here, which would now leave a consumed unit with no
    /// recorded cost.
    /// </summary>
    [Fact]
    public async Task A_refunded_sale_is_costed_exactly_as_a_completed_one_is()
    {
        var store = new FakeStore();
        var sale = Sale(1, productId: 10, nayaxCost: 1.10m,
            transactionStatusId: NayaxTransactionStatusIds.Refunded);

        var cost = await new CostSale(store, LedgerCost(10, 1.50m).Object).Handle(sale);

        Assert.Equal(
            new SaleCostAssignment(1.50m, SaleCostStatus.Costed, SaleCostOrigin.InventoryLedger), cost);
    }

    /// <summary>
    /// And a refunded sale the review excludes is costed by neither rule: a sale the business's own
    /// <c>lastSales</c> contradicted, or that its Owner excluded, says nothing reliable about what
    /// left the machine.
    /// </summary>
    [Theory]
    [InlineData(NayaxSqsVerification.Conflict)]
    [InlineData(NayaxSqsVerification.OwnerExcluded)]
    public async Task A_refunded_sale_excluded_by_review_is_left_uncosted(NayaxSqsVerification excluded)
    {
        var store = new FakeStore();
        var sale = Sale(1, productId: 10, nayaxCost: 1.10m,
            transactionStatusId: NayaxTransactionStatusIds.Refunded, sqsVerification: excluded);

        var cost = await new CostSale(store, LedgerCost(10, 1.50m).Object).Handle(sale, force: true);

        Assert.Equal(new SaleCostAssignment(null, SaleCostStatus.Pending, SaleCostOrigin.Unknown), cost);
    }

    [Fact]
    public async Task Backfill_dry_run_reports_without_staging_or_saving_and_apply_persists()
    {
        var store = new FakeStore
        {
            Sales =
            {
                Sale(1, productId: 10, nayaxCost: null),
                Sale(2, productId: null, nayaxCost: null, productName: "Unknown drink"),
                Sale(3, productId: 10, nayaxCost: 1m, unitCost: 1m, status: SaleCostStatus.Costed),
            },
        };
        var backfill = new BackfillSaleCosts(store, new CostSale(store, LedgerCost(10, 1.50m).Object));

        var dryRun = await backfill.Handle(dryRun: true);

        Assert.Equal(new SaleCostingBackfillResult(1, 0, 0, 1, 1, true), dryRun);
        Assert.False(store.LastLoadForUpdate);
        Assert.Empty(store.Staged);
        Assert.Equal(0, store.SaveCount);

        var applied = await backfill.Handle(dryRun: false, force: true);

        Assert.Equal(new SaleCostingBackfillResult(2, 0, 0, 1, 0, false), applied);
        Assert.True(store.LastLoadForUpdate);
        Assert.Equal(1, store.SaveCount);
        Assert.Equal(new SaleCostAssignment(1.50m, SaleCostStatus.Costed, SaleCostOrigin.InventoryLedger), store.Staged[1]);
        Assert.Equal(new SaleCostAssignment(null, SaleCostStatus.Error, SaleCostOrigin.Unknown), store.Staged[2]);
        Assert.Equal(new SaleCostAssignment(1.50m, SaleCostStatus.Costed, SaleCostOrigin.InventoryLedger), store.Staged[3]);
    }

    [Fact]
    public async Task Unforced_backfill_counts_finalized_sales_and_does_not_recost_them()
    {
        var store = new FakeStore
        {
            Sales =
            {
                Sale(1, productId: 10, nayaxCost: null, unitCost: 0.80m, status: SaleCostStatus.LegacyEstimated),
                Sale(2, productId: 10, nayaxCost: null),
            },
        };
        var backfill = new BackfillSaleCosts(store, new CostSale(store, LedgerCost(10, 1.50m).Object));

        var result = await backfill.Handle(dryRun: false);

        Assert.Equal(new SaleCostingBackfillResult(1, 0, 0, 0, 1, false), result);
        Assert.Equal([2L], store.Staged.Keys);
    }

    [Fact]
    public async Task Repeating_an_applied_backfill_yields_the_same_costs()
    {
        var store = new FakeStore { Sales = { Sale(1, productId: 10, nayaxCost: 1m) } };
        var backfill = new BackfillSaleCosts(store, new CostSale(store, LedgerCost(10, 1.50m).Object));

        await backfill.Handle(dryRun: false, force: true);
        var first = store.Staged[1];
        store.Staged.Clear();
        await backfill.Handle(dryRun: false, force: true);

        Assert.Equal(first, store.Staged[1]);
    }

    [Fact]
    public async Task Pending_sales_costing_filters_by_the_persisted_association_and_saves_once()
    {
        var store = new FakeStore()
        {
            Sales =
            {
                Sale(1, productId: 10, nayaxCost: null),
                Sale(2, productId: 20, nayaxCost: 1m),
                // Unresolved, and its name is the filtered product's: it is still not that
                // product's sale (issue #758).
                Sale(3, productId: null, nayaxCost: null, productName: "Snack"),
            },
        };
        var useCase = new CostPendingSales(store, new CostSale(store, LedgerCost(10, 1.50m).Object));

        var costed = await useCase.Handle(productId: 10);

        Assert.Equal(1, costed);
        Assert.True(store.LastSelection!.PendingOnly);
        Assert.Equal([1L], store.Staged.Keys.Order());
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task Pending_sales_costing_with_nothing_pending_saves_nothing()
    {
        var store = new FakeStore();

        Assert.Equal(0, await new CostPendingSales(store, new CostSale(store, LedgerCost(10, 1m).Object)).Handle());
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task Historical_backfill_dry_run_stages_nothing_and_apply_costs_only_eligible_sales()
    {
        var store = new FakeStore
        {
            Sales =
            {
                Sale(1, productId: 10, nayaxCost: 1.10m),
                Sale(2, productId: 10, nayaxCost: 1.20m, status: SaleCostStatus.Error),
                Sale(3, productId: 10, nayaxCost: -1m),
                Sale(4, productId: 10, nayaxCost: null),
                Sale(5, productId: 10, nayaxCost: 1.30m, unitCost: 0.90m, status: SaleCostStatus.Costed),
            },
        };
        var backfill = new BackfillNayaxHistoricalSaleCosts(store);

        var dryRun = await backfill.Handle(dryRun: true);

        Assert.Equal(new NayaxCostBackfillResult(5, 4, 2, 1, 2, 1, 0, true), dryRun);
        Assert.False(store.LastLoadForUpdate);
        Assert.Empty(store.Staged);
        Assert.Equal(0, store.SaveCount);

        var applied = await backfill.Handle(dryRun: false);

        Assert.Equal(new NayaxCostBackfillResult(5, 4, 2, 1, 2, 1, 0, false), applied);
        Assert.Equal(1, store.SaveCount);
        Assert.Equal([1L, 2L], store.Staged.Keys.Order());
        Assert.Equal(new SaleCostAssignment(1.10m, SaleCostStatus.Costed, SaleCostOrigin.NayaxTransactionExport), store.Staged[1]);
        Assert.Equal(new SaleCostAssignment(1.20m, SaleCostStatus.Costed, SaleCostOrigin.NayaxTransactionExport), store.Staged[2]);
    }

    /// <summary>
    /// The stored cost here has no recorded provenance, which is precedence step 4 - the weakest
    /// claim there is - so the Nayax export cost (step 3) is an improvement and is written. Since
    /// issue #1261 that is the whole of what <c>force</c> may do to a costed sale: improve its
    /// precedence, or correct a cost of the same provenance.
    /// </summary>
    [Fact]
    public async Task Forced_historical_backfill_replaces_a_final_cost_of_unknown_provenance()
    {
        var store = new FakeStore
        {
            Sales = { Sale(5, productId: 10, nayaxCost: 1.30m, unitCost: 0.90m, status: SaleCostStatus.Costed) },
        };

        var result = await new BackfillNayaxHistoricalSaleCosts(store).Handle(dryRun: false, force: true);

        Assert.Equal(1, result.SalesWouldBeCosted);
        Assert.Equal(new SaleCostAssignment(1.30m, SaleCostStatus.Costed, SaleCostOrigin.NayaxTransactionExport), store.Staged[5]);
    }

    /// <summary>
    /// Issue #1261, the defect: <c>force</c> used to mean "overwrite", so this tool wrote the
    /// transaction-level Nayax cost (precedence step 3) over a sale costed from the inventory ledger
    /// (step 1) or from the latest costed purchase before it (step 2) - replacing a better cost with
    /// a worse one, in the business's own historical COGS. It now fills only a sale no better step
    /// has costed.
    /// </summary>
    [Theory]
    [InlineData(SaleCostOrigin.InventoryLedger)]
    [InlineData(SaleCostOrigin.Estimated)]
    public async Task A_forced_historical_backfill_leaves_a_better_sourced_cost_alone(SaleCostOrigin source)
    {
        var store = new FakeStore
        {
            Sales =
            {
                Sale(1, productId: 10, nayaxCost: 1.30m, unitCost: 0.90m, status: SaleCostStatus.Costed, source: source),
                Sale(2, productId: 10, nayaxCost: 1.40m),
            },
        };

        var result = await new BackfillNayaxHistoricalSaleCosts(store).Handle(dryRun: false, force: true);

        Assert.Equal(1, result.SalesWouldBeCosted);
        Assert.Equal(1, result.SalesAlreadyCosted);
        Assert.Equal([2L], store.Staged.Keys);
        Assert.Equal(new SaleCostAssignment(1.40m, SaleCostStatus.Costed, SaleCostOrigin.NayaxTransactionExport), store.Staged[2]);
    }

    /// <summary>
    /// Equal precedence is the one case where the value decides: a corrected Nayax export cost is
    /// written, and the same cost re-read is not - so a repeated forced run stages nothing and
    /// enqueues no rebuild.
    /// </summary>
    [Fact]
    public async Task A_forced_historical_backfill_rewrites_an_equal_source_cost_only_when_it_differs()
    {
        var store = new FakeStore
        {
            Sales =
            {
                Sale(1, productId: 10, nayaxCost: 1.30m, unitCost: 1.30m, status: SaleCostStatus.Costed,
                    source: SaleCostOrigin.NayaxTransactionExport),
                Sale(2, productId: 10, nayaxCost: 1.45m, unitCost: 1.30m, status: SaleCostStatus.Costed,
                    source: SaleCostOrigin.NayaxTransactionExport),
            },
        };

        var result = await new BackfillNayaxHistoricalSaleCosts(store).Handle(dryRun: false, force: true);

        Assert.Equal(1, result.SalesWouldBeCosted);
        Assert.Equal([2L], store.Staged.Keys);
        Assert.Equal(new SaleCostAssignment(1.45m, SaleCostStatus.Costed, SaleCostOrigin.NayaxTransactionExport), store.Staged[2]);
    }

    /// <summary>
    /// Issue #1261: a forced ledger backfill applies the complete four-step precedence. The sale's
    /// product has no ledger cost before it, but a costed purchase was replayed earlier, so the sale
    /// takes that purchase's unit cost as <see cref="SaleCostOrigin.Estimated"/> (step 2) instead of
    /// falling through to its own Nayax cost (step 3) - which is what the sale-time path did, and
    /// what made this tool downgrade a step-2 estimate it re-costed.
    /// </summary>
    [Fact]
    public async Task A_forced_backfill_costs_a_sale_from_the_latest_purchase_before_its_Nayax_cost()
    {
        var store = new FakeStore { Sales = { Sale(1, productId: 10, nayaxCost: 1.10m) } };
        var backfill = new BackfillSaleCosts(store, new CostSale(store, LedgerCost(10, null, latestPurchase: 1.60m).Object));

        var result = await backfill.Handle(dryRun: false, force: true);

        Assert.Equal(new SaleCostingBackfillResult(1, 0, 0, 0, 0, false), result);
        Assert.Equal(new SaleCostAssignment(1.60m, SaleCostStatus.Costed, SaleCostOrigin.Estimated), store.Staged[1]);
    }

    /// <summary>
    /// And with step 2 applied, a sale already costed from the latest purchase is re-costed to the
    /// same estimate, so nothing is staged: the backfill no longer rewrites it at the Nayax cost.
    /// A ledger-costed sale is left alone the same way, and only the uncosted sale is filled.
    /// </summary>
    [Fact]
    public async Task A_forced_backfill_never_replaces_a_better_sourced_cost()
    {
        var store = new FakeStore
        {
            Sales =
            {
                Sale(1, productId: 10, nayaxCost: 1.10m, unitCost: 1.60m, status: SaleCostStatus.Costed,
                    source: SaleCostOrigin.Estimated),
                Sale(2, productId: 10, nayaxCost: 1.10m, unitCost: 2.50m, status: SaleCostStatus.Costed,
                    source: SaleCostOrigin.InventoryLedger),
                Sale(3, productId: 10, nayaxCost: 1.10m),
            },
        };
        var backfill = new BackfillSaleCosts(store, new CostSale(store, LedgerCost(10, null, latestPurchase: 1.60m).Object));

        var result = await backfill.Handle(dryRun: false, force: true);

        Assert.Equal(new SaleCostingBackfillResult(1, 0, 0, 0, 2, false), result);
        Assert.Equal([3L], store.Staged.Keys);
        Assert.Equal(new SaleCostAssignment(1.60m, SaleCostStatus.Costed, SaleCostOrigin.Estimated), store.Staged[3]);
    }

    /// <summary>
    /// A sale no step can cost is left exactly as it is - no staged write and no cost rebuild asked
    /// for - and is still reported as pending rather than as finalized: the run changed nothing
    /// about it, which is not the same as having costed it.
    /// </summary>
    [Fact]
    public async Task A_forced_backfill_stages_nothing_for_a_sale_it_leaves_unchanged()
    {
        var store = new FakeStore { Sales = { Sale(1, productId: 10, nayaxCost: null) } };
        var backfill = new BackfillSaleCosts(store, new CostSale(store, LedgerCost(10, null).Object));

        var result = await backfill.Handle(dryRun: false, force: true);

        Assert.Equal(new SaleCostingBackfillResult(0, 0, 1, 0, 0, false), result);
        Assert.Empty(store.Staged);
    }

    /// <summary>
    /// A corrected ledger cost is equal precedence with a different value, so it is written: the
    /// precedence gate protects a better source, not a stale number.
    /// </summary>
    [Fact]
    public async Task A_forced_backfill_rewrites_a_ledger_cost_that_changed()
    {
        var store = new FakeStore
        {
            Sales =
            {
                Sale(1, productId: 10, nayaxCost: 1.10m, unitCost: 2.50m, status: SaleCostStatus.Costed,
                    source: SaleCostOrigin.InventoryLedger),
            },
        };
        var backfill = new BackfillSaleCosts(store, new CostSale(store, LedgerCost(10, 2.75m).Object));

        await backfill.Handle(dryRun: false, force: true);

        Assert.Equal(new SaleCostAssignment(2.75m, SaleCostStatus.Costed, SaleCostOrigin.InventoryLedger), store.Staged[1]);
    }

    [Fact]
    public async Task Historical_backfill_passes_the_range_and_excludes_unresolved_sales_from_a_product_filter()
    {
        var from = SaleAt.AddDays(-1);
        var to = SaleAt.AddDays(1);
        var store = new FakeStore
        {
            Sales =
            {
                Sale(1, productId: 10, nayaxCost: 1.10m),
                Sale(2, productId: null, nayaxCost: 1.20m, productName: "Unknown drink"),
            },
        };

        var result = await new BackfillNayaxHistoricalSaleCosts(store).Handle(
            dryRun: false, from: from, to: to, productId: 10);

        Assert.Equal(new StockConsumingSaleSelection(false, from, to), store.LastSelection);
        Assert.Equal(1, result.SalesReviewed);
        Assert.Equal([1L], store.Staged.Keys);
    }

    [Fact]
    public async Task A_Nayax_upstream_failure_cannot_fail_or_partially_apply_the_historical_backfill()
    {
        // The historical backfill recovers costs from the Nayax export cost already persisted on each
        // sale; it never calls the Nayax API. Every Nayax read fails here, and the backfills resolved
        // through the real Application registrations still complete and never touch the client.
        var nayax = new Mock<INayaxLynxClient>(MockBehavior.Strict);
        var store = new FakeStore { Sales = { Sale(1, productId: 10, nayaxCost: 1.10m) } };
        var services = new ServiceCollection()
            .AddApplicationServices()
            .AddSingleton(nayax.Object)
            .AddSingleton<ISaleCostingStore>(store)
            .AddSingleton(Mock.Of<IInventoryCostLedgerStore>());
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var historical = await scope.ServiceProvider.GetRequiredService<BackfillNayaxHistoricalSaleCosts>()
            .Handle(dryRun: false);
        var backfill = await scope.ServiceProvider.GetRequiredService<BackfillSaleCosts>()
            .Handle(dryRun: true, force: true);

        Assert.Equal(1, historical.SalesWouldBeCosted);
        Assert.Equal(new SaleCostAssignment(1.10m, SaleCostStatus.Costed, SaleCostOrigin.NayaxTransactionExport), store.Staged[1]);
        Assert.Equal(1, backfill.CostedCount);
        nayax.VerifyNoOtherCalls();
    }

    private static Mock<IRebuildProductCost> LedgerCost(long productId, decimal? cost, decimal? latestPurchase = null)
    {
        var rebuild = new Mock<IRebuildProductCost>();
        rebuild.Setup(x => x.GetAverageUnitCostAtAsync(
                productId, It.IsAny<DateTime>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(cost);
        rebuild.Setup(x => x.GetLatestPurchaseUnitCostBeforeAsync(
                productId, It.IsAny<DateTime>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(latestPurchase);
        return rebuild;
    }

    private static CostableSale Sale(
        long transactionId,
        long? productId,
        decimal? nayaxCost,
        decimal? unitCost = null,
        SaleCostStatus status = SaleCostStatus.Pending,
        string? productName = "Snack",
        int? transactionStatusId = NayaxTransactionStatusIds.Completed,
        NayaxSqsVerification? sqsVerification = null,
        SaleCostOrigin source = SaleCostOrigin.Unknown) =>
        new()
        {
            TransactionId = transactionId,
            TransactionStatusId = transactionStatusId,
            SqsVerification = sqsVerification,
            // The persisted association (issue #758) and, beside it, the raw provider id the sale
            // arrived with. Costing reads only the former.
            ProductId = productId,
            NayaxProductId = productId,
            ProductName = productName,
            AuthorizationTime = SaleAt,
            NayaxProductCostPrice = nayaxCost,
            UnitCostAtSale = unitCost,
            CostOfGoodsSold = unitCost,
            CostingStatus = status,
            CostSource = source,
        };

    private sealed class FakeStore : ISaleCostingStore
    {
        public List<CostableSale> Sales { get; } = [];
        public Dictionary<long, DateTime> Cutoffs { get; } = [];
        public Dictionary<long, SaleCostAssignment> Staged { get; } = [];
        public StockConsumingSaleSelection? LastSelection { get; private set; }
        public bool LastLoadForUpdate { get; private set; }
        public int SaveCount { get; private set; }

        public Task<DateTime?> GetTransitionCutoffAsync(long productId, CancellationToken cancellationToken) =>
            Task.FromResult(Cutoffs.TryGetValue(productId, out var cutoff) ? cutoff : (DateTime?)null);

        public Task<IReadOnlyList<CostableSale>> LoadStockConsumingSalesAsync(
            StockConsumingSaleSelection selection, bool forUpdate, CancellationToken cancellationToken)
        {
            LastSelection = selection;
            LastLoadForUpdate = forUpdate;
            return Task.FromResult<IReadOnlyList<CostableSale>>(Sales
                .Where(s => !selection.PendingOnly || s.CostingStatus == SaleCostStatus.Pending)
                .ToList());
        }

        public void StageCost(CostableSale sale, SaleCostAssignment cost) => Staged[sale.TransactionId] = cost;

        public Task<SaleCostSaveOutcome> SaveChangesAsync(CancellationToken cancellationToken)
        {
            SaveCount++;
            return Task.FromResult(SaleCostSaveOutcome.NothingRefused);
        }
    }
}
