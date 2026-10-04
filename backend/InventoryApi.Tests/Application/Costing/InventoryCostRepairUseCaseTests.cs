using Inventory.Application.Costing;
using Inventory.Application.Tenancy;
using Inventory.Domain.Costing;
using Inventory.Domain.Exceptions;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Tenancy;
using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Application.Costing;

/// <summary>
/// Orchestration of the costing-repair use cases (issue #359): what the preview reports, what it
/// refuses, and what the apply refuses to write - a stale ledger, a repair that does not replay
/// before the sale it must cover, a repair that leaves the history incomplete, and a caller it
/// cannot attribute the repair to.
///
/// The seeded product is the situation a repair exists for: it entered the inventory-cost
/// transition with an opening costing quantity of zero while stock was still in the machines, so
/// its two later completed sales have no costed stock to consume and every write to the product
/// fails on the same fatal issue.
/// </summary>
public class InventoryCostRepairUseCaseTests
{
    private const long ProductId = 10;
    private const string Reason = "Machine stock at the 2026 cutover was never costed.";
    private static readonly DateTime Now = new(2026, 2, 10, 4, 0, 0, DateTimeKind.Utc);
    private static readonly ActorIdentity Operator = Actor("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Preview_reports_the_position_before_and_after_the_repair_and_the_projected_history()
    {
        await using var db = await SeedAsync();

        var preview = await TestCostingUseCases.PreviewRepair(db).Handle(Request());

        Assert.Equal(ProductId, preview.ProductId);
        Assert.Equal("Coke Zero", preview.ProductName);
        Assert.Equal(Day(2), preview.EffectiveAt);
        Assert.Equal((4, 2m, 8m, Reason), (preview.Quantity, preview.UnitCost, preview.TotalValue, preview.Reason));
        Assert.Equal((0, 0m), (preview.CostingQuantityBefore, preview.InventoryValueBefore));
        Assert.Equal((4, 8m, 2m), (preview.CostingQuantityAfter, preview.InventoryValueAfter, preview.AverageUnitCostAfter));
        Assert.Equal(new UncostableSale(1, Day(3)), preview.FirstUncostableSale);
        Assert.True(preview.ReplaysBeforeFirstUncostableSale);
        Assert.Equal((2, 4m, 2m), (preview.ProjectedCostingQuantity, preview.ProjectedInventoryValue, preview.ProjectedAverageUnitCost));
        Assert.Empty(preview.RemainingFatalIssues);
        Assert.NotEmpty(preview.LedgerFingerprint);
    }

    [Fact]
    public async Task Preview_reports_the_fatal_issue_a_partial_repair_would_leave_behind()
    {
        await using var db = await SeedAsync();

        var preview = await TestCostingUseCases.PreviewRepair(db).Handle(Request(quantity: 1));

        Assert.Equal(new UncostableSale(1, Day(3)), preview.FirstUncostableSale);
        Assert.Equal(0, preview.ProjectedCostingQuantity);
        var issue = Assert.Single(preview.RemainingFatalIssues);
        Assert.Equal(CostDataQualityIssueCodes.UnknownCost, issue.Code);
        Assert.Contains("Completed Nayax sale 2", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_persists_nothing_at_all()
    {
        await using var db = await SeedAsync();

        await TestCostingUseCases.PreviewRepair(db).Handle(Request());

        Assert.Empty(await db.InventoryCostRepairs.ToListAsync());
        var product = await db.Products.AsNoTracking().SingleAsync();
        Assert.Null(product.CostingQuantity);
        Assert.Null(product.InventoryValue);
    }

    [Fact]
    public async Task Preview_and_apply_reject_an_unknown_product()
    {
        await using var db = await SeedAsync();

        var preview = await Assert.ThrowsAsync<DomainValidationException>(() =>
            TestCostingUseCases.PreviewRepair(db).Handle(Request() with { ProductId = 999 }));
        var apply = await Assert.ThrowsAsync<DomainValidationException>(() =>
            TestCostingUseCases.ApplyRepair(db, Identified()).Handle(ApplyRequest("ignored") with { ProductId = 999 }));

        Assert.Equal("Product 999 does not exist.", preview.Message);
        Assert.Equal("Product 999 does not exist.", apply.Message);
    }

    [Theory]
    [InlineData(0, 1, Reason, "A costing repair must add a positive quantity.")]
    [InlineData(-3, 1, Reason, "A costing repair must add a positive quantity.")]
    [InlineData(4, -1, Reason, "A costing repair unit cost cannot be negative.")]
    [InlineData(4, 1, "", "Record a specific reason for this costing repair: at least 10 characters, and not a placeholder.")]
    [InlineData(4, 1, "correction", "Record a specific reason for this costing repair: at least 10 characters, and not a placeholder.")]
    public async Task Preview_rejects_an_invalid_repair_before_reading_anything(
        int quantity,
        int unitCost,
        string reason,
        string message)
    {
        await using var db = await SeedAsync();

        var exception = await Assert.ThrowsAsync<DomainValidationException>(() =>
            TestCostingUseCases.PreviewRepair(db)
                .Handle(new(ProductId, Day(2), quantity, unitCost, reason)));

        Assert.Equal(message, exception.Message);
    }

    [Fact]
    public async Task Preview_rejects_an_effective_time_at_or_before_the_transition_cutoff()
    {
        await using var db = await SeedAsync();
        var preview = TestCostingUseCases.PreviewRepair(db);

        var atCutoff = await Assert.ThrowsAsync<DomainValidationException>(() =>
            preview.Handle(Request() with { EffectiveAt = Day(1) }));
        var beforeCutoff = await Assert.ThrowsAsync<DomainValidationException>(() =>
            preview.Handle(Request() with { EffectiveAt = Day(1).AddTicks(-1) }));

        Assert.Contains("must take effect after the product's inventory-cost transition cutoff", atCutoff.Message, StringComparison.Ordinal);
        Assert.Equal(atCutoff.Message, beforeCutoff.Message);
    }

    [Fact]
    public async Task Apply_appends_the_repair_recosts_the_sales_and_leaves_the_physical_history_untouched()
    {
        await using var db = await SeedAsync();
        var preview = await TestCostingUseCases.PreviewRepair(db).Handle(Request());

        var applied = await TestCostingUseCases.ApplyRepair(db, Identified(), clock: new FakeClock(Now))
            .Handle(ApplyRequest(preview.LedgerFingerprint));

        Assert.Equal((2, 4m, 2m, 2), (applied.CostingQuantity, applied.InventoryValue, applied.AverageUnitCost, applied.RecostedSaleCount));
        var stored = await db.InventoryCostRepairs.AsNoTracking().SingleAsync();
        Assert.Equal(applied.Repair.Id, stored.Id);
        Assert.Equal((ProductId, Day(2), 4, 2m, 8m, Reason), (stored.ProductId, stored.EffectiveAt, stored.Quantity, stored.UnitCost, stored.TotalValue, stored.Reason));
        Assert.Equal(Now, stored.CreatedAt);
        Assert.Equal(Operator.DirectoryTenantId, stored.CreatedByDirectoryTenantId);
        Assert.Equal(Operator.ObjectId, stored.CreatedByObjectId);
        var product = await db.Products.AsNoTracking().SingleAsync();
        Assert.Equal(10, product.QuantityInStock);
        Assert.Equal((2, 4m, 2m), (product.CostingQuantity, product.InventoryValue, product.AverageUnitCost));
        Assert.All(await db.NayaxSales.AsNoTracking().ToListAsync(), sale =>
        {
            Assert.Equal(2m, sale.UnitCostAtSale);
            Assert.Equal(SaleCostingStatus.Costed, sale.CostingStatus);
            Assert.Equal(SaleCostSource.InventoryLedger, sale.CostSource);
        });
        Assert.Empty(await db.StockAdjustments.AsNoTracking().ToListAsync());
        var baseline = await db.InventoryCostTransitionBaselines.AsNoTracking().SingleAsync();
        Assert.Equal((Day(1), 10, 0, 0m), (baseline.CutoffAt, baseline.HomeStockQuantity, baseline.OpeningCostingQuantity, baseline.InventoryValue));
    }

    /// <summary>
    /// Issue #359: the stale-preview model. The authoritative read, the comparison against the
    /// fingerprint the operator approved, and the write are one operation, so a sale, purchase,
    /// count or earlier repair that lands in between invalidates the projection instead of being
    /// silently repaired over.
    /// </summary>
    [Fact]
    public async Task Apply_rejects_a_preview_whose_ledger_changed_underneath_it()
    {
        await using var db = await SeedAsync();
        var preview = await TestCostingUseCases.PreviewRepair(db).Handle(Request());
        db.NayaxSales.Add(Sale(3, Day(4).AddHours(1)));
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<DomainValidationException>(() =>
            TestCostingUseCases.ApplyRepair(db, Identified()).Handle(ApplyRequest(preview.LedgerFingerprint)));

        Assert.Equal(
            "This product's cost history changed after the preview. Run the preview again before applying the repair.",
            exception.Message);
        Assert.Empty(await db.InventoryCostRepairs.ToListAsync());
    }

    [Fact]
    public async Task Apply_rejects_a_fingerprint_that_does_not_match_the_current_ledger()
    {
        await using var db = await SeedAsync();
        var apply = TestCostingUseCases.ApplyRepair(db, Identified());

        await Assert.ThrowsAsync<DomainValidationException>(() => apply.Handle(ApplyRequest("")));
        await Assert.ThrowsAsync<DomainValidationException>(() => apply.Handle(ApplyRequest(new string('0', 64))));

        Assert.Empty(await db.InventoryCostRepairs.ToListAsync());
    }

    /// <summary>
    /// Issue #359: placement is verified through the replay's own ordering, because a repair's
    /// effective time (UTC) and a sale's authorization time (machine-local) are not in the same time
    /// zone. A repair the replay reaches after the sale cannot cost it, so the apply refuses it
    /// instead of recording a repair that changes nothing it was asked for.
    /// </summary>
    [Fact]
    public async Task Apply_rejects_a_repair_that_does_not_replay_before_the_first_uncostable_sale()
    {
        await using var db = await SeedAsync();
        var preview = await TestCostingUseCases.PreviewRepair(db).Handle(Request(effectiveAt: Day(5)));
        Assert.False(preview.ReplaysBeforeFirstUncostableSale);

        var exception = await Assert.ThrowsAsync<DomainValidationException>(() =>
            TestCostingUseCases.ApplyRepair(db, Identified())
                .Handle(ApplyRequest(preview.LedgerFingerprint) with { EffectiveAt = Day(5) }));

        Assert.Equal(
            "The repair does not replay before completed sale 1, so it cannot cost that sale. "
                + "Choose an earlier effective time and preview again.",
            exception.Message);
        Assert.Empty(await db.InventoryCostRepairs.ToListAsync());
    }

    [Fact]
    public async Task Apply_refuses_a_caller_it_cannot_attribute_the_repair_to()
    {
        await using var db = await SeedAsync();
        var preview = await TestCostingUseCases.PreviewRepair(db).Handle(Request());

        await Assert.ThrowsAsync<BusinessAccessDeniedException>(() =>
            TestCostingUseCases.ApplyRepair(db, FakeAuthenticatedActorAccessor.Unidentifiable())
                .Handle(ApplyRequest(preview.LedgerFingerprint)));
        await Assert.ThrowsAsync<BusinessAccessDeniedException>(() =>
            TestCostingUseCases.ApplyRepair(db, FakeAuthenticatedActorAccessor.NotAuthenticated())
                .Handle(ApplyRequest(preview.LedgerFingerprint)));

        Assert.Empty(await db.InventoryCostRepairs.ToListAsync());
    }

    [Fact]
    public async Task A_second_repair_previews_and_applies_on_top_of_the_first()
    {
        await using var db = await SeedAsync();
        var first = await TestCostingUseCases.PreviewRepair(db).Handle(Request(quantity: 1));
        db.InventoryCostRepairs.Add(StoredRepair(Day(2), 1));
        await db.SaveChangesAsync();

        var second = await TestCostingUseCases.PreviewRepair(db).Handle(Request(quantity: 1, effectiveAt: Day(2)));

        Assert.NotEqual(first.LedgerFingerprint, second.LedgerFingerprint);
        Assert.Equal((1, 2m), (second.CostingQuantityBefore, second.InventoryValueBefore));
        Assert.Equal((2, 4m, 2m), (second.CostingQuantityAfter, second.InventoryValueAfter, second.AverageUnitCostAfter));
        Assert.Empty(second.RemainingFatalIssues);

        var applied = await TestCostingUseCases.ApplyRepair(db, Identified(), clock: new FakeClock(Now))
            .Handle(ApplyRequest(second.LedgerFingerprint) with { Quantity = 1 });

        Assert.Equal(0, applied.CostingQuantity);
        Assert.Equal(2, applied.RecostedSaleCount);
        Assert.Equal(2, await db.InventoryCostRepairs.CountAsync());
    }

    /// <summary>
    /// Newest effective repair first, and within one instant the most recently recorded one, so a
    /// history read as an audit trail shows the latest state of the repaired period at the top.
    /// </summary>
    [Fact]
    public async Task History_returns_a_products_repairs_newest_first_and_rejects_an_unknown_product()
    {
        await using var db = await SeedAsync();
        db.InventoryCostRepairs.AddRange(
            StoredRepair(Day(2), 1),
            StoredRepair(Day(2).AddHours(1), 2),
            StoredRepair(Day(2), 3));
        await db.SaveChangesAsync();

        var history = await TestCostingUseCases.RepairHistory(db).Handle(ProductId);
        var unknown = await Assert.ThrowsAsync<DomainValidationException>(() =>
            TestCostingUseCases.RepairHistory(db).Handle(999));

        Assert.Equal(
            [(Day(2).AddHours(1), 2), (Day(2), 3), (Day(2), 1)],
            history.Select(x => (x.EffectiveAt, x.Quantity)));
        Assert.All(history, repair =>
        {
            Assert.Equal(ProductId, repair.ProductId);
            Assert.Equal(Reason, repair.Reason);
            Assert.Equal(Now, repair.CreatedAt);
            Assert.Equal(Operator.DirectoryTenantId, repair.CreatedByDirectoryTenantId);
            Assert.Equal(Operator.ObjectId, repair.CreatedByObjectId);
            Assert.True(repair.Id > 0);
        });
        Assert.Equal("Product 999 does not exist.", unknown.Message);
    }

    private static InventoryCostRepair StoredRepair(DateTime effectiveAt, int quantity) =>
        new()
        {
            ProductId = ProductId,
            EffectiveAt = effectiveAt,
            Quantity = quantity,
            UnitCost = 2m,
            TotalValue = quantity * 2m,
            Reason = Reason,
            CreatedAt = Now,
            CreatedByDirectoryTenantId = Operator.DirectoryTenantId,
            CreatedByObjectId = Operator.ObjectId
        };

    private static InventoryCostRepairRequest Request(int quantity = 4, decimal unitCost = 2m, DateTime? effectiveAt = null) =>
        new(ProductId, effectiveAt ?? Day(2), quantity, unitCost, Reason);

    private static ApplyInventoryCostRepairRequest ApplyRequest(string fingerprint) =>
        new(ProductId, Day(2), 4, 2m, Reason, fingerprint);

    private static FakeAuthenticatedActorAccessor Identified() => FakeAuthenticatedActorAccessor.Identified(Operator);

    private static ActorIdentity Actor(string objectId)
    {
        Assert.True(ActorIdentity.TryCreate("33333333-3333-3333-3333-333333333333", objectId, out var actor));
        return actor!;
    }

    private static async Task<AppDbContext> SeedAsync()
    {
        var db = TestAppDbContext.Unrestricted(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Products.Add(new Product { Id = ProductId, Name = "Coke Zero", QuantityInStock = 10 });
        db.InventoryCostTransitionBaselines.Add(new InventoryCostTransitionBaseline
        {
            ProductId = ProductId,
            CutoffAt = Day(1),
            HomeStockQuantity = 10,
            OpeningCostingQuantity = 0,
            InventoryValue = 0m,
            AverageUnitCost = 0m
        });
        db.NayaxSales.AddRange(Sale(1, Day(3)), Sale(2, Day(4)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return db;
    }

    private static NayaxSales Sale(long transactionId, DateTime at) =>
        new()
        {
            TransactionID = transactionId,
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
            MachineID = 1,
            NayaxProductId = ProductId,
            MachineAuthorizationTime = at
        };

    private static DateTime Day(int day) => new(2026, 1, day, 12, 0, 0, DateTimeKind.Utc);
}
