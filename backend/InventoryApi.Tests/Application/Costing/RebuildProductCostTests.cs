using Inventory.Application.Costing;
using Inventory.Domain.Costing;
using InventoryApi.Data;
using InventoryApi.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;
using DomainStock = Inventory.Domain.Stock;

using Inventory.Domain.FinancialConfiguration;

namespace InventoryApi.Tests.Application.Costing;

public class RebuildProductCostTests
{
    [Fact]
    public async Task Rebuild_keeps_machine_refills_out_of_business_cost_quantity()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 1, Name = "Snack", QuantityInStock = 12 });
        db.StockAdjustments.AddRange(
            Movement(1, 10, 2m, StockAdjustmentReason.Restock, Day(1)),
            Movement(1, -8, null, StockAdjustmentReason.MachineRefill, Day(2)),
            Movement(1, 10, 4m, StockAdjustmentReason.Restock, Day(3)));
        await db.SaveChangesAsync();

        await TestCostingUseCases.Rebuild(db).RebuildAsync(1);
        await db.SaveChangesAsync();

        var product = await db.Products.SingleAsync();
        Assert.Equal(12, product.QuantityInStock);
        Assert.Equal(20, product.CostingQuantity);
        Assert.Equal(60m, product.InventoryValue);
        Assert.Equal(3m, product.AverageUnitCost);
        var refill = await db.StockAdjustments.SingleAsync(movement => movement.Reason == StockAdjustmentReason.MachineRefill);
        Assert.Equal(10, refill.CostingQuantityAfter);
        Assert.Equal(20m, refill.InventoryValueAfter);
        Assert.Equal(2m, refill.AverageUnitCostAfter);
    }

    [Fact]
    public async Task Rebuild_consumes_completed_sales_and_recosts_from_changed_date()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 1, Name = "Snack", QuantityInStock = 20 });
        db.StockAdjustments.Add(Movement(1, 10, 2m, StockAdjustmentReason.Restock, Day(1)));
        db.NayaxSales.AddRange(Enumerable.Range(1, 8).Select(index => Sale(index, Day(1).AddMinutes(index))));
        db.StockAdjustments.Add(Movement(1, 10, 4m, StockAdjustmentReason.Restock, Day(2)));
        await db.SaveChangesAsync();

        var result = await TestCostingUseCases.Rebuild(db).RebuildAsync(1, Day(1));
        await db.SaveChangesAsync();

        var product = await db.Products.SingleAsync();
        Assert.Equal(12, product.CostingQuantity);
        Assert.Equal(44m, product.InventoryValue);
        Assert.Equal(44m / 12m, product.AverageUnitCost);
        Assert.Equal(8, result.RecostedSaleCount);
        Assert.All(await db.NayaxSales.ToListAsync(), sale =>
        {
            Assert.Equal(2m, sale.UnitCostAtSale);
            Assert.Equal(SaleCostingStatus.Costed, sale.CostingStatus);
        });
    }

    [Fact]
    public async Task Rebuild_only_consumes_sales_when_machine_refills_precede_sales()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 1, Name = "Snack", QuantityInStock = 2 });
        db.StockAdjustments.AddRange(
            Movement(1, 10, 2m, StockAdjustmentReason.Restock, Day(1)),
            Movement(1, -8, null, StockAdjustmentReason.MachineRefill, Day(2)));
        db.NayaxSales.AddRange(Sale(1, Day(3)), Sale(2, Day(3).AddMinutes(1)), Sale(3, Day(3).AddMinutes(2)));
        await db.SaveChangesAsync();

        await TestCostingUseCases.Rebuild(db).RebuildAsync(1, Day(1));
        await db.SaveChangesAsync();

        var product = await db.Products.SingleAsync();
        Assert.Equal(2, product.QuantityInStock);
        Assert.Equal(7, product.CostingQuantity);
        Assert.Equal(14m, product.InventoryValue);
    }

    [Fact]
    public async Task Rebuild_applies_cost_only_purchase_edit()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 1, Name = "Snack", QuantityInStock = 10 });
        var purchase = Movement(1, 10, 1m, StockAdjustmentReason.Restock, Day(1));
        db.StockAdjustments.Add(purchase);
        await db.SaveChangesAsync();
        var rebuild = TestCostingUseCases.Rebuild(db);

        await rebuild.RebuildAsync(1, Day(1));
        purchase.UnitCost = 2m;
        await rebuild.RebuildAsync(1, Day(1));
        await db.SaveChangesAsync();

        var product = await db.Products.SingleAsync();
        Assert.Equal(10, product.CostingQuantity);
        Assert.Equal(20m, product.InventoryValue);
        Assert.Equal(2m, product.AverageUnitCost);
    }

    [Fact]
    public async Task Rebuild_historical_purchase_change_recosts_later_sales_and_purchases()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 1, Name = "Snack", QuantityInStock = 20 });
        var opening = Movement(1, 10, 2m, StockAdjustmentReason.Restock, Day(1));
        db.StockAdjustments.AddRange(opening, Movement(1, 10, 4m, StockAdjustmentReason.Restock, Day(3)));
        db.NayaxSales.Add(Sale(1, Day(2)));
        await db.SaveChangesAsync();
        var rebuild = TestCostingUseCases.Rebuild(db);

        await rebuild.RebuildAsync(1, Day(1));
        opening.UnitCost = 1m;
        await rebuild.RebuildAsync(1, Day(1));
        await db.SaveChangesAsync();

        var product = await db.Products.SingleAsync();
        Assert.Equal(19, product.CostingQuantity);
        Assert.Equal(49m, product.InventoryValue);
        Assert.Equal(49m / 19m, product.AverageUnitCost);
        Assert.Equal(1m, (await db.NayaxSales.SingleAsync()).CostOfGoodsSold);
    }

    [Fact]
    public async Task Dry_run_reports_missing_opening_without_mutating_product()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 1, Name = "Snack", QuantityInStock = 10, AverageUnitCost = 2m });
        db.NayaxSales.Add(Sale(1, Day(2)));
        await db.SaveChangesAsync();

        var result = await TestCostingUseCases.Rebuild(db).RebuildAsync(1, Day(1), dryRun: true);

        Assert.Contains(result.Issues, issue => issue.Code == "MissingOpening");
        var product = await db.Products.SingleAsync();
        Assert.Equal(10, product.QuantityInStock);
        Assert.Null(product.CostingQuantity);
        Assert.Null(product.InventoryValue);
    }

    [Fact]
    public async Task Rebuild_costs_historical_positive_correction_at_baseline_average()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 1, Name = "Snack", QuantityInStock = 8 });
        db.InventoryCostTransitionBaselines.Add(Baseline(1, homeStockQuantity: 4, openingCostingQuantity: 8, inventoryValue: 14m, Day(1)));
        db.StockAdjustments.Add(Movement(1, 4, null, StockAdjustmentReason.Correction, Day(2)));
        await db.SaveChangesAsync();

        var result = await TestCostingUseCases.Rebuild(db).RebuildAsync(1);
        await db.SaveChangesAsync();

        var product = await db.Products.SingleAsync();
        var correction = await db.StockAdjustments.SingleAsync();
        Assert.DoesNotContain(result.Issues, issue => issue.Code == "UnknownCost");
        Assert.Equal(8, product.QuantityInStock);
        Assert.Equal(12, product.CostingQuantity);
        Assert.Equal(21m, product.InventoryValue);
        Assert.Equal(1.75m, product.AverageUnitCost);
        Assert.Equal(1.75m, correction.UnitCost);
        Assert.Equal(7m, correction.TotalCost);
    }

    [Fact]
    public async Task Rebuild_costs_negative_correction_at_historical_average()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 1, Name = "Snack", QuantityInStock = 2 });
        db.InventoryCostTransitionBaselines.Add(Baseline(1, homeStockQuantity: 4, openingCostingQuantity: 8, inventoryValue: 14m, Day(1)));
        db.StockAdjustments.Add(Movement(1, -2, null, StockAdjustmentReason.Correction, Day(2)));
        await db.SaveChangesAsync();

        await TestCostingUseCases.Rebuild(db).RebuildAsync(1);
        await db.SaveChangesAsync();

        var product = await db.Products.SingleAsync();
        var correction = await db.StockAdjustments.SingleAsync();
        Assert.Equal(6, product.CostingQuantity);
        Assert.Equal(10.50m, product.InventoryValue);
        Assert.Equal(1.75m, product.AverageUnitCost);
        Assert.Equal(1.75m, correction.UnitCost);
        Assert.Equal(3.50m, correction.TotalCost);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public async Task Rebuild_reports_fatal_issue_for_correction_without_known_average(int quantityChange)
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 1, Name = "Snack", QuantityInStock = 2 });
        db.InventoryCostTransitionBaselines.Add(Baseline(1, homeStockQuantity: 2, openingCostingQuantity: 0, inventoryValue: 0m, Day(1)));
        db.StockAdjustments.Add(Movement(1, quantityChange, null, StockAdjustmentReason.Correction, Day(2)));
        await db.SaveChangesAsync();

        var result = await TestCostingUseCases.Rebuild(db).RebuildAsync(1, dryRun: true);

        Assert.Contains(result.Issues, issue => issue.Code == "UnknownCost");
        var correction = await db.StockAdjustments.SingleAsync();
        Assert.Null(correction.UnitCost);
        Assert.Null(correction.TotalCost);
    }

    /// <summary>
    /// Issue #359: the point of a costing repair. The product entered the cutover with an opening
    /// costing quantity of zero while stock was still in the machines, so its later completed sales
    /// have no costed stock to consume and the replay reports <c>UnknownCost</c> for each of them.
    /// A repair effective before the sales restores the costing history, and only then do they cost.
    /// </summary>
    [Fact]
    public async Task Rebuild_costs_previously_uncostable_sales_from_a_costing_repair()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 1, Name = "Snack", QuantityInStock = 10 });
        db.InventoryCostTransitionBaselines.Add(Baseline(1, homeStockQuantity: 10, openingCostingQuantity: 0, inventoryValue: 0m, Day(1)));
        db.NayaxSales.AddRange(Sale(1, Day(3)), Sale(2, Day(4)));
        await db.SaveChangesAsync();
        var rebuild = TestCostingUseCases.Rebuild(db);

        var before = await rebuild.RebuildAsync(1, Day(2), dryRun: true);
        db.InventoryCostRepairs.Add(Repair(1, quantity: 4, unitCost: 2m, Day(2)));
        await db.SaveChangesAsync();
        var after = await rebuild.RebuildAsync(1, Day(2));
        await db.SaveChangesAsync();

        Assert.Equal(2, before.Issues.Count);
        Assert.All(before.Issues, issue => Assert.Equal(CostDataQualityIssueCodes.UnknownCost, issue.Code));
        Assert.Empty(after.Issues);
        Assert.Equal(2, after.RecostedSaleCount);
        var product = await db.Products.SingleAsync();
        Assert.Equal(10, product.QuantityInStock);
        Assert.Equal(2, product.CostingQuantity);
        Assert.Equal(4m, product.InventoryValue);
        Assert.Equal(2m, product.AverageUnitCost);
        Assert.All(await db.NayaxSales.ToListAsync(), sale =>
        {
            Assert.Equal(2m, sale.UnitCostAtSale);
            Assert.Equal(2m, sale.CostOfGoodsSold);
            Assert.Equal(SaleCostingStatus.Costed, sale.CostingStatus);
            Assert.Equal(SaleCostSource.InventoryLedger, sale.CostSource);
        });
        Assert.Empty(await db.StockAdjustments.ToListAsync());
    }

    /// <summary>
    /// Issue #359: a repair that explains only part of the missing history leaves the fatal issue
    /// in place, and a fatal issue still means the rebuild stages nothing at all (issue #362) - which
    /// is what lets the apply use case roll back and persist no repair.
    /// </summary>
    [Fact]
    public async Task A_repair_that_covers_only_part_of_the_history_leaves_the_fatal_issue_and_stages_nothing()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 1, Name = "Snack", QuantityInStock = 10 });
        db.InventoryCostTransitionBaselines.Add(Baseline(1, homeStockQuantity: 10, openingCostingQuantity: 0, inventoryValue: 0m, Day(1)));
        db.NayaxSales.AddRange(Sale(1, Day(3)), Sale(2, Day(4)));
        db.InventoryCostRepairs.Add(Repair(1, quantity: 1, unitCost: 2m, Day(2)));
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<InventoryCostDataQualityException>(
            () => TestCostingUseCases.Rebuild(db).RebuildAsync(1, Day(2)));

        Assert.Contains("Completed Nayax sale 2", exception.Message, StringComparison.Ordinal);
        var product = await db.Products.SingleAsync();
        Assert.Null(product.CostingQuantity);
        Assert.Null(product.InventoryValue);
        Assert.All(await db.NayaxSales.ToListAsync(), sale => Assert.Null(sale.UnitCostAtSale));
    }

    /// <summary>
    /// Issue #359: a repair adds costing quantity and value and nothing else. A restock still carries
    /// its own purchase cost, a machine refill still moves physical stock without consuming costing
    /// inventory, and a correction is still costed at the current weighted average - which now
    /// includes the repair, because that is what repairing the costing history means.
    /// </summary>
    [Fact]
    public async Task A_repair_leaves_restock_machine_refill_and_correction_semantics_unchanged()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 1, Name = "Snack", QuantityInStock = 1 });
        db.StockAdjustments.AddRange(
            Movement(1, 10, 2m, StockAdjustmentReason.Restock, Day(1)),
            Movement(1, -8, null, StockAdjustmentReason.MachineRefill, Day(3)),
            Movement(1, -1, null, StockAdjustmentReason.Correction, Day(4)));
        db.InventoryCostRepairs.Add(Repair(1, quantity: 2, unitCost: 5m, Day(2)));
        await db.SaveChangesAsync();

        var result = await TestCostingUseCases.Rebuild(db).RebuildAsync(1);
        await db.SaveChangesAsync();

        Assert.DoesNotContain(result.Issues, issue => CostDataQualityIssueCodes.IsFatal(issue.Code));
        var movements = await db.StockAdjustments.OrderBy(x => x.EffectiveAt).ToListAsync();
        Assert.Equal((2m, 20m, 10, 10, 20m), (movements[0].UnitCost, movements[0].TotalCost,
            movements[0].QuantityAfter, movements[0].CostingQuantityAfter, movements[0].InventoryValueAfter));
        Assert.Equal((null, null, 2, 12, 30m), (movements[1].UnitCost, movements[1].TotalCost,
            movements[1].QuantityAfter, movements[1].CostingQuantityAfter, movements[1].InventoryValueAfter));
        Assert.Equal((2.5m, 2.5m, 1, 11, 27.5m), (movements[2].UnitCost, movements[2].TotalCost,
            movements[2].QuantityAfter, movements[2].CostingQuantityAfter, movements[2].InventoryValueAfter));
        var product = await db.Products.SingleAsync();
        Assert.Equal((1, 11, 27.5m, 2.5m), (product.QuantityInStock, product.CostingQuantity, product.InventoryValue, product.AverageUnitCost));
    }

    [Fact]
    public async Task Repeated_rebuilds_are_idempotent()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 1, Name = "Snack", QuantityInStock = 17 });
        db.StockAdjustments.AddRange(
            Movement(1, 10, 2m, StockAdjustmentReason.Restock, Day(1)),
            Movement(1, -3, null, StockAdjustmentReason.Correction, Day(2)),
            Movement(1, 10, 3m, StockAdjustmentReason.Restock, Day(3)));
        db.NayaxSales.AddRange(Sale(1, Day(2).AddHours(1)), Sale(2, Day(4)));
        await db.SaveChangesAsync();
        var rebuild = TestCostingUseCases.Rebuild(db);

        var first = await rebuild.RebuildAsync(1, Day(1));
        await db.SaveChangesAsync();
        var afterFirst = await Snapshot(db);
        var second = await rebuild.RebuildAsync(1, Day(1));
        await db.SaveChangesAsync();

        Assert.Equal(afterFirst, await Snapshot(db));
        Assert.Equal(first.CostingQuantity, second.CostingQuantity);
        Assert.Equal(first.InventoryValue, second.InventoryValue);
        Assert.Equal(first.AverageUnitCost, second.AverageUnitCost);
        Assert.Equal(2, second.RecostedSaleCount);
        Assert.Equal(15, second.CostingQuantity);
    }

    [Fact]
    public async Task Fatal_data_quality_issue_throws_and_never_stages_the_product_position()
    {
        var store = new FakeLedgerStore(new InventoryCostLedger(
            new CostReplayProduct(1, 0, null, null),
            [new CostReplayAdjustment(1, Day(1), DomainStock.StockAdjustmentReason.Restock, 5, null, false)],
            [],
            [],
            null));

        var exception = await Assert.ThrowsAsync<InventoryCostDataQualityException>(
            () => new RebuildProductCost(store).RebuildAsync(1));

        Assert.Contains("has no valid unit cost", exception.Message);
        Assert.Null(store.StagedPosition);
    }

    /// <summary>
    /// Issue #362: a real rebuild must decide before it writes anything. A fatal history leaves the
    /// caller's unit of work completely untouched - no replayed movement or sale cost and no product
    /// position - so a batch caller can save the products that rebuilt cleanly after catching this.
    /// </summary>
    [Fact]
    public async Task Fatal_data_quality_issue_stages_neither_the_replay_nor_the_product_position()
    {
        var store = new FakeLedgerStore(new InventoryCostLedger(
            new CostReplayProduct(1, 0, null, null),
            [new CostReplayAdjustment(1, Day(1), DomainStock.StockAdjustmentReason.Restock, 1, 2m, true)],
            [new CostReplaySale(9, Day(2)), new CostReplaySale(10, Day(3))],
            [],
            null));

        var exception = await Assert.ThrowsAsync<InventoryCostDataQualityException>(
            () => new RebuildProductCost(store).RebuildAsync(1, Day(1)));

        Assert.Contains("has no known opening cost", exception.Message);
        Assert.False(store.ReplayStaged);
        Assert.Null(store.StagedPosition);
    }

    [Fact]
    public async Task Dry_run_stages_nothing_and_does_not_throw_for_a_fatal_issue()
    {
        var store = new FakeLedgerStore(new InventoryCostLedger(
            new CostReplayProduct(1, 0, null, null),
            [new CostReplayAdjustment(1, Day(1), DomainStock.StockAdjustmentReason.Restock, 5, null, false)],
            [new CostReplaySale(9, Day(2))],
            [],
            null));

        var result = await new RebuildProductCost(store).RebuildAsync(1, Day(1), dryRun: true);

        Assert.True(result.DryRun);
        Assert.Equal(0, result.RecostedSaleCount);
        Assert.Contains(result.Issues, issue => issue.Code == CostDataQualityIssueCodes.UnknownCost);
        Assert.False(store.ForUpdate);
        Assert.False(store.ReplayStaged);
        Assert.Null(store.StagedPosition);
    }

    /// <summary>
    /// Issue #359: the costing-only rebuild a repair apply uses stages the costing position and the
    /// recosted sale costs, and deliberately stages no physical quantity (null leaves the stored one
    /// alone) and no movement outcome, so a repair cannot restate physical stock, a
    /// <c>StockAdjustment</c> row or MachineRefill history. It still replays the whole ledger, so it
    /// reports the replayed physical quantity it did not persist.
    /// </summary>
    [Fact]
    public async Task Costing_only_rebuild_stages_the_costing_position_and_sale_costs_and_nothing_physical()
    {
        var store = new FakeLedgerStore(DriftedLedger());

        var result = await new RebuildProductCost(store).RebuildCostingOnlyAsync(1, Day(1));

        Assert.Equal(10, result.PhysicalQuantity);
        Assert.Equal(1, result.RecostedSaleCount);
        Assert.Equal(new ProductCostPosition(null, 9, 18m, 2m), store.StagedPosition);
        Assert.Empty(store.StagedAdjustments);
        Assert.Equal(2m, Assert.Single(store.StagedSaleCosts).UnitCost);
    }

    /// <summary>
    /// The complement of the test above: a normal rebuild - what the purchase, count, refill and
    /// sales-sync writes still call - keeps synchronising the product's physical quantity and every
    /// movement's running position from the replay.
    /// </summary>
    [Fact]
    public async Task Rebuild_still_stages_the_replayed_physical_quantity_and_movement_positions()
    {
        var store = new FakeLedgerStore(DriftedLedger());

        await new RebuildProductCost(store).RebuildAsync(1, Day(1));

        Assert.Equal(new ProductCostPosition(10, 9, 18m, 2m), store.StagedPosition);
        Assert.Equal(10, Assert.Single(store.StagedAdjustments).QuantityAfter);
    }

    /// <summary>
    /// Issue #362 holds for the costing-only rebuild too: it decides before it stages, so the repair
    /// apply's transaction has nothing to roll back when the repaired history is still fatal.
    /// </summary>
    [Fact]
    public async Task Costing_only_rebuild_stages_nothing_for_a_fatal_history()
    {
        var store = new FakeLedgerStore(new InventoryCostLedger(
            new CostReplayProduct(1, 0, 0, 0m),
            [new CostReplayAdjustment(1, Day(1), DomainStock.StockAdjustmentReason.Restock, 5, null, false)],
            [new CostReplaySale(9, Day(2))],
            [],
            null));

        var exception = await Assert.ThrowsAsync<InventoryCostDataQualityException>(
            () => new RebuildProductCost(store).RebuildCostingOnlyAsync(1, Day(1)));

        Assert.Contains("has no valid unit cost", exception.Message);
        Assert.False(store.ReplayStaged);
        Assert.Null(store.StagedPosition);
    }

    /// <summary>
    /// A product whose stored physical quantity (7) does not match what its movements replay to
    /// (10), so staging the physical quantity is visible rather than a no-op. Its costing quantity
    /// and value are stored as zero so the stored-versus-replayed physical reconciliation issue -
    /// which only applies to a product with no costing position at all - stays out of the way.
    /// </summary>
    private static InventoryCostLedger DriftedLedger() =>
        new(
            new CostReplayProduct(1, 7, 0, 0m),
            [new CostReplayAdjustment(1, Day(1), DomainStock.StockAdjustmentReason.Restock, 10, 2m, true)],
            [new CostReplaySale(9, Day(2))],
            [],
            null);

    [Fact]
    public async Task Rebuild_of_an_unknown_product_throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new RebuildProductCost(new FakeLedgerStore(null)).RebuildAsync(1));
    }

    [Fact]
    public async Task Average_unit_cost_at_a_time_is_null_for_an_unknown_product_or_a_fatal_history()
    {
        Assert.Null(await new RebuildProductCost(new FakeLedgerStore(null)).GetAverageUnitCostAtAsync(1, Day(5)));

        var fatal = new FakeLedgerStore(new InventoryCostLedger(
            new CostReplayProduct(1, 0, null, null),
            [new CostReplayAdjustment(1, Day(1), DomainStock.StockAdjustmentReason.Restock, 5, null, false)],
            [],
            [],
            null));
        Assert.Null(await new RebuildProductCost(fatal).GetAverageUnitCostAtAsync(1, Day(5)));
    }

    private static async Task<string> Snapshot(AppDbContext db)
    {
        var product = await db.Products.AsNoTracking().SingleAsync();
        var adjustments = await db.StockAdjustments.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        var sales = await db.NayaxSales.AsNoTracking().OrderBy(x => x.TransactionID).ToListAsync();
        return string.Join("|",
            new[] { $"{product.QuantityInStock},{product.CostingQuantity},{product.InventoryValue},{product.AverageUnitCost}" }
                .Concat(adjustments.Select(x => $"{x.Id},{x.QuantityAfter},{x.CostingQuantityAfter},{x.InventoryValueAfter},{x.AverageUnitCostAfter},{x.UnitCost},{x.TotalCost}"))
                .Concat(sales.Select(x => $"{x.TransactionID},{x.UnitCostAtSale},{x.CostOfGoodsSold},{x.CostingStatus},{x.CostSource}")));
    }

    private sealed class FakeLedgerStore(InventoryCostLedger? ledger) : IInventoryCostLedgerStore
    {
        public bool? ForUpdate { get; private set; }
        public bool ReplayStaged { get; private set; }
        public IReadOnlyCollection<CostReplayAdjustmentOutcome> StagedAdjustments { get; private set; } = [];
        public IReadOnlyCollection<CostReplaySaleCost> StagedSaleCosts { get; private set; } = [];
        public ProductCostPosition? StagedPosition { get; private set; }

        public Task<InventoryCostLedger?> LoadAsync(long productId, bool forUpdate, CancellationToken cancellationToken)
        {
            ForUpdate = forUpdate;
            return Task.FromResult(ledger);
        }

        public Task<InventoryCostLedger?> LoadAsOfAsync(long productId, DateTime asOf, CancellationToken cancellationToken) =>
            Task.FromResult(ledger);

        public void StageReplay(
            InventoryCostLedger ledger,
            IReadOnlyCollection<CostReplayAdjustmentOutcome> adjustments,
            IReadOnlyCollection<CostReplaySaleCost> recostedSales)
        {
            ReplayStaged = true;
            StagedAdjustments = adjustments;
            StagedSaleCosts = recostedSales;
        }

        public void StageProductPosition(InventoryCostLedger ledger, ProductCostPosition position) => StagedPosition = position;
    }

    private static AppDbContext CreateDb() =>
        TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static StockAdjustment Movement(long productId, int quantity, decimal? unitCost, StockAdjustmentReason reason, DateTime effectiveAt) =>
        new()
        {
            ProductId = productId,
            QuantityChange = quantity,
            Reason = reason,
            UnitCost = unitCost,
            EffectiveAt = effectiveAt
        };

    private static InventoryCostTransitionBaseline Baseline(
        long productId,
        int homeStockQuantity,
        int openingCostingQuantity,
        decimal inventoryValue,
        DateTime cutoffAt) =>
        new()
        {
            ProductId = productId,
            HomeStockQuantity = homeStockQuantity,
            OpeningCostingQuantity = openingCostingQuantity,
            InventoryValue = inventoryValue,
            AverageUnitCost = openingCostingQuantity > 0 ? inventoryValue / openingCostingQuantity : 0m,
            CutoffAt = cutoffAt
        };

    private static InventoryCostRepair Repair(long productId, int quantity, decimal unitCost, DateTime effectiveAt) =>
        new()
        {
            ProductId = productId,
            Quantity = quantity,
            UnitCost = unitCost,
            TotalValue = quantity * unitCost,
            Reason = "Opening costing quantity was understated at the cutover.",
            EffectiveAt = effectiveAt,
            CreatedAt = effectiveAt,
            CreatedByDirectoryTenantId = "11111111-1111-1111-1111-111111111111",
            CreatedByObjectId = "22222222-2222-2222-2222-222222222222"
        };

    private static NayaxSales Sale(long id, DateTime at) =>
        new()
        {
            TransactionID = id,
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
            MachineID = 1,
            NayaxProductId = 1,
            MachineAuthorizationTime = at
        };

    private static DateTime Day(int day) => new(2026, 1, day, 12, 0, 0, DateTimeKind.Utc);
}
