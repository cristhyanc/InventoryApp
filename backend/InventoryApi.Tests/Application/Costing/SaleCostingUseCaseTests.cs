using Inventory.Application;
using Inventory.Application.Costing;
using Inventory.Application.Nayax;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Reporting.ProductMatching;
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
        var store = new FakeStore(Product(10, "Snack"));
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
        var store = new FakeStore(Product(10, "Snack"));
        var sale = Sale(1, productId: 10, nayaxCost: 1.20m, unitCost: 0.90m, status: SaleCostStatus.Costed);

        var cost = await new CostSale(store, LedgerCost(10, 1.50m).Object).Handle(sale, force: true);

        Assert.Equal(new SaleCostAssignment(1.50m, SaleCostStatus.Costed, SaleCostOrigin.InventoryLedger), cost);
    }

    [Fact]
    public async Task A_costed_sale_missing_its_cost_values_is_recosted_without_force()
    {
        var store = new FakeStore(Product(10, "Snack"));
        var sale = Sale(1, productId: 10, nayaxCost: 1.20m, unitCost: null, status: SaleCostStatus.Costed);

        var cost = await new CostSale(store, LedgerCost(10, null).Object).Handle(sale);

        Assert.Equal(new SaleCostAssignment(1.20m, SaleCostStatus.Costed, SaleCostOrigin.NayaxTransactionExport), cost);
    }

    [Fact]
    public async Task An_unmatched_product_without_a_Nayax_cost_is_an_error_and_never_reads_the_ledger()
    {
        var store = new FakeStore(Product(10, "Snack"));
        var rebuild = LedgerCost(10, 1.50m);
        var sale = Sale(1, productId: 99, nayaxCost: null, productName: "Unknown drink");

        var cost = await new CostSale(store, rebuild.Object).Handle(sale);

        Assert.Equal(new SaleCostAssignment(null, SaleCostStatus.Error, SaleCostOrigin.Unknown), cost);
        rebuild.Verify(x => x.GetAverageUnitCostAtAsync(
            It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task An_unmatched_product_with_a_Nayax_cost_is_costed_from_the_Nayax_export()
    {
        var store = new FakeStore(Product(10, "Snack"));
        var sale = Sale(1, productId: 99, nayaxCost: 1.10m, productName: "Unknown drink");

        var cost = await new CostSale(store, LedgerCost(10, 1.50m).Object).Handle(sale);

        Assert.Equal(new SaleCostAssignment(1.10m, SaleCostStatus.Costed, SaleCostOrigin.NayaxTransactionExport), cost);
    }

    [Fact]
    public async Task A_sale_matched_by_name_uses_the_Domain_product_matcher()
    {
        var store = new FakeStore(Product(10, "Snack"));
        var sale = Sale(1, productId: null, nayaxCost: null, productName: "Snack (3.00)");

        var cost = await new CostSale(store, LedgerCost(10, 1.50m).Object).Handle(sale);

        Assert.Equal(new SaleCostAssignment(1.50m, SaleCostStatus.Costed, SaleCostOrigin.InventoryLedger), cost);
    }

    [Fact]
    public async Task A_sale_at_or_before_the_transition_cutoff_is_not_costed_from_the_ledger()
    {
        var store = new FakeStore(Product(10, "Snack")) { Cutoffs = { [10] = SaleAt } };
        var sale = Sale(1, productId: 10, nayaxCost: null);

        var cost = await new CostSale(store, LedgerCost(10, 1.50m).Object).Handle(sale);

        Assert.Equal(new SaleCostAssignment(null, SaleCostStatus.Pending, SaleCostOrigin.Unknown), cost);
    }

    [Fact]
    public async Task A_sale_that_is_not_completed_is_left_uncosted_and_pending_even_when_forced()
    {
        var store = new FakeStore(Product(10, "Snack"));
        var sale = Sale(1, productId: 10, nayaxCost: 1.10m, unitCost: 1m, status: SaleCostStatus.Costed,
            transactionStatusId: NayaxTransactionStatusIds.Refunded);

        var cost = await new CostSale(store, LedgerCost(10, 1.50m).Object).Handle(sale, force: true);

        Assert.Equal(new SaleCostAssignment(null, SaleCostStatus.Pending, SaleCostOrigin.Unknown), cost);
    }

    [Fact]
    public async Task Backfill_dry_run_reports_without_staging_or_saving_and_apply_persists()
    {
        var store = new FakeStore(Product(10, "Snack"))
        {
            Sales =
            {
                Sale(1, productId: 10, nayaxCost: null),
                Sale(2, productId: 99, nayaxCost: null, productName: "Unknown drink"),
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
        var store = new FakeStore(Product(10, "Snack"))
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
        var store = new FakeStore(Product(10, "Snack")) { Sales = { Sale(1, productId: 10, nayaxCost: 1m) } };
        var backfill = new BackfillSaleCosts(store, new CostSale(store, LedgerCost(10, 1.50m).Object));

        await backfill.Handle(dryRun: false, force: true);
        var first = store.Staged[1];
        store.Staged.Clear();
        await backfill.Handle(dryRun: false, force: true);

        Assert.Equal(first, store.Staged[1]);
    }

    [Fact]
    public async Task Pending_sales_costing_filters_by_matched_product_and_saves_once()
    {
        var store = new FakeStore(Product(10, "Snack"), Product(20, "Drink"))
        {
            Sales =
            {
                Sale(1, productId: 10, nayaxCost: null),
                Sale(2, productId: 20, nayaxCost: 1m),
                Sale(3, productId: null, nayaxCost: null, productName: "Snack"),
            },
        };
        var useCase = new CostPendingSales(store, new CostSale(store, LedgerCost(10, 1.50m).Object));

        var costed = await useCase.Handle(productId: 10);

        Assert.Equal(2, costed);
        Assert.True(store.LastSelection!.PendingOnly);
        Assert.Equal([1L, 3L], store.Staged.Keys.Order());
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task Pending_sales_costing_with_nothing_pending_saves_nothing()
    {
        var store = new FakeStore(Product(10, "Snack"));

        Assert.Equal(0, await new CostPendingSales(store, new CostSale(store, LedgerCost(10, 1m).Object)).Handle());
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task Historical_backfill_dry_run_stages_nothing_and_apply_costs_only_eligible_sales()
    {
        var store = new FakeStore(Product(10, "Snack"))
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

    [Fact]
    public async Task Forced_historical_backfill_replaces_a_final_cost_with_the_Nayax_export_cost()
    {
        var store = new FakeStore(Product(10, "Snack"))
        {
            Sales = { Sale(5, productId: 10, nayaxCost: 1.30m, unitCost: 0.90m, status: SaleCostStatus.Costed) },
        };

        var result = await new BackfillNayaxHistoricalSaleCosts(store).Handle(dryRun: false, force: true);

        Assert.Equal(1, result.SalesWouldBeCosted);
        Assert.Equal(new SaleCostAssignment(1.30m, SaleCostStatus.Costed, SaleCostOrigin.NayaxTransactionExport), store.Staged[5]);
    }

    [Fact]
    public async Task Historical_backfill_passes_the_range_and_excludes_unmatched_products_from_a_product_filter()
    {
        var from = SaleAt.AddDays(-1);
        var to = SaleAt.AddDays(1);
        var store = new FakeStore(Product(10, "Snack"))
        {
            Sales =
            {
                Sale(1, productId: 10, nayaxCost: 1.10m),
                Sale(2, productId: 99, nayaxCost: 1.20m, productName: "Unknown drink"),
            },
        };

        var result = await new BackfillNayaxHistoricalSaleCosts(store).Handle(
            dryRun: false, from: from, to: to, productId: 10);

        Assert.Equal(new CompletedSaleSelection(false, from, to), store.LastSelection);
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
        var store = new FakeStore(Product(10, "Snack")) { Sales = { Sale(1, productId: 10, nayaxCost: 1.10m) } };
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

    private static ProductMatchCandidate Product(long id, string name) => new(id, name);

    private static Mock<IRebuildProductCost> LedgerCost(long productId, decimal? cost)
    {
        var rebuild = new Mock<IRebuildProductCost>();
        rebuild.Setup(x => x.GetAverageUnitCostAtAsync(
                productId, It.IsAny<DateTime>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(cost);
        return rebuild;
    }

    private static CostableSale Sale(
        long transactionId,
        long? productId,
        decimal? nayaxCost,
        decimal? unitCost = null,
        SaleCostStatus status = SaleCostStatus.Pending,
        string? productName = "Snack",
        int? transactionStatusId = NayaxTransactionStatusIds.Completed) =>
        new(transactionId, transactionStatusId, productId, productName, SaleAt, nayaxCost, unitCost, unitCost, status);

    private sealed class FakeStore(params ProductMatchCandidate[] products) : ISaleCostingStore
    {
        public List<CostableSale> Sales { get; } = [];
        public Dictionary<long, DateTime> Cutoffs { get; } = [];
        public Dictionary<long, SaleCostAssignment> Staged { get; } = [];
        public CompletedSaleSelection? LastSelection { get; private set; }
        public bool LastLoadForUpdate { get; private set; }
        public int SaveCount { get; private set; }

        public Task<IReadOnlyList<ProductMatchCandidate>> GetProductCandidatesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProductMatchCandidate>>(products);

        public Task<DateTime?> GetTransitionCutoffAsync(long productId, CancellationToken cancellationToken) =>
            Task.FromResult(Cutoffs.TryGetValue(productId, out var cutoff) ? cutoff : (DateTime?)null);

        public Task<IReadOnlyList<CostableSale>> LoadCompletedSalesAsync(
            CompletedSaleSelection selection, bool forUpdate, CancellationToken cancellationToken)
        {
            LastSelection = selection;
            LastLoadForUpdate = forUpdate;
            return Task.FromResult<IReadOnlyList<CostableSale>>(Sales
                .Where(s => !selection.PendingOnly || s.CostingStatus == SaleCostStatus.Pending)
                .ToList());
        }

        public void StageCost(CostableSale sale, SaleCostAssignment cost) => Staged[sale.TransactionId] = cost;

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            SaveCount++;
            return Task.CompletedTask;
        }
    }
}
