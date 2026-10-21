using Inventory.Application.PickList;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Stock;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using DomainStock = Inventory.Domain.Stock;

// Both layers declare these enums (the shared wire vocabulary documented on
// StockAdjustmentResponseMapper); the persisted rows this test reads back carry the persistence one.
using StockAdjustmentReason = Inventory.Infrastructure.Models.StockAdjustmentReason;
using StockAdjustmentSource = Inventory.Infrastructure.Models.StockAdjustmentSource;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The batch refill end to end over a real relational database (issue #743): the whole use case on
/// <see cref="EfPickListRefillStore"/>, the Pick List's own storage read and the real machines
/// adapter, over SQLite.
///
/// <para>Relational rather than InMemory, deliberately. What this slice is about is a transaction:
/// every row commits together or none does, the key's uniqueness is the database's rule as much as
/// the use case's, and a refill leaves ordinary <c>StockAdjustment</c> rows whose costing columns
/// are the established <c>MachineRefill</c> ones. None of that is provable without real SQL
/// (AGENTS.md § Database and migrations). The <c>BEGIN IMMEDIATE</c> write lock needs two genuine
/// writers and is in <see cref="PickListRefillConcurrencyTests"/>.</para>
/// </summary>
public class EfPickListRefillStoreTests
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;
    private const long MachineA = 40;
    private const long MachineB = 41;

    private static readonly DateTime RecordedAt = new(2026, 10, 20, 2, 30, 0, DateTimeKind.Utc);

    private const string Key = "a2f0c4b8-3f4e-4d9a-8e1b-77f0b2c6d311";

    private static async Task<DbContextOptions<AppDbContext>> CreateSqliteAsync(SqliteConnection connection)
    {
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var setup = TestAppDbContext.Unrestricted(options);
        await setup.Database.EnsureCreatedAsync();
        return options;
    }

    private static RecordPickListRefill UseCaseFor(AppDbContext db) =>
        new(
            new EfPickListRefillStore(
                db, TestCostingUseCases.RecordMovement(db), TestCostingUseCases.Rebuild(db)),
            new EfPickListStorageStockStore(db),
            new EfLocalMachineStore(db),
            new FakeClock(RecordedAt));

    private static PickListRefillInput Input(
        string? key = Key,
        long? machineId = null,
        string? notes = null,
        params (long ProductId, int Quantity)[] rows) =>
        new(
            key,
            machineId,
            notes,
            [.. rows.Select(row => new PickListRefillRequestRow(row.ProductId, row.Quantity))]);

    /// <summary>
    /// Seeds a product with an opening costed Restock, so <c>RebuildProductCost</c>'s replay has a
    /// known average cost: it recomputes costing purely from the movement history, not from whatever
    /// a test writes on <see cref="Product"/>.
    /// </summary>
    private static async Task SeedProductAsync(
        DbContextOptions<AppDbContext> options,
        int businessId,
        long productId,
        int quantity,
        decimal unitCost = 2m,
        bool isActive = true)
    {
        await using var seed = TestAppDbContext.For(options, businessId);
        seed.Products.Add(new Product
        {
            Id = productId,
            Name = $"Product {productId}",
            QuantityInStock = quantity,
            IsActive = isActive,
        });
        seed.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = productId,
            QuantityChange = quantity,
            QuantityAfter = quantity,
            Reason = StockAdjustmentReason.Restock,
            UnitCost = unitCost,
            EffectiveAt = RecordedAt.AddDays(-1),
        });
        await seed.SaveChangesAsync();
    }

    /// <summary>
    /// Two answers describe the same recorded refill. Compared field by field because the record's
    /// own equality compares its row list by reference, and a replay reads its rows back from the
    /// database rather than handing back the instance the original built.
    /// </summary>
    private static void AssertSameRefill(PickListRefillRecord expected, PickListRefillRecord actual)
    {
        Assert.Equal(expected.IdempotencyKey, actual.IdempotencyKey);
        Assert.Equal(expected.MachineId, actual.MachineId);
        Assert.Equal(expected.Notes, actual.Notes);
        Assert.Equal(expected.RecordedAtUtc, actual.RecordedAtUtc);
        Assert.Equal(expected.Rows, actual.Rows);
    }

    private static async Task SeedMachineAsync(
        DbContextOptions<AppDbContext> options, int businessId, long machineId, bool isActive = true)
    {
        await using var seed = TestAppDbContext.For(options, businessId);
        seed.Machines.Add(new LocalMachine { Id = machineId, Name = $"Machine {machineId}", IsActive = isActive });
        await seed.SaveChangesAsync();
    }

    #region Recording the batch

    /// <summary>
    /// The criterion itself: one request, one <c>MachineRefill</c> movement per row, storage down by
    /// what was taken, and the batch recorded against the key that sent it.
    /// </summary>
    [Fact]
    public async Task A_multi_row_refill_records_one_machine_refill_movement_per_row()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedProductAsync(options, BusinessA, 100, quantity: 10);
        await SeedProductAsync(options, BusinessA, 101, quantity: 4);
        await SeedMachineAsync(options, BusinessA, MachineA);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var attempt = await UseCaseFor(db).Handle(
                Input(machineId: MachineA, notes: "Friday run", rows: [(100, 3), (101, 4)]),
                CancellationToken.None);

            Assert.Equal(PickListRefillOutcome.Recorded, attempt.Outcome);
            Assert.Equal(RecordedAt, attempt.Record!.RecordedAtUtc);
            Assert.Equal([(100L, 3), (101L, 4)], attempt.Record.Rows.Select(row => (row.ProductId, row.QuantityTaken)));
            Assert.All(attempt.Record.Rows, row => Assert.True(row.StockAdjustmentId > 0));
        }

        await using var verify = TestAppDbContext.For(options, BusinessA);
        Assert.Equal(7, (await verify.Products.SingleAsync(p => p.Id == 100)).QuantityInStock);
        Assert.Equal(0, (await verify.Products.SingleAsync(p => p.Id == 101)).QuantityInStock);

        var movements = await verify.StockAdjustments
            .Where(adjustment => adjustment.Reason == StockAdjustmentReason.MachineRefill)
            .OrderBy(adjustment => adjustment.Id)
            .ToListAsync();

        Assert.Equal([-3, -4], movements.Select(movement => movement.QuantityChange));
        Assert.All(movements, movement =>
        {
            Assert.Equal(StockAdjustmentSource.Manual, movement.Source);
            Assert.Equal(MachineA, movement.MachineId);
            Assert.Equal("Friday run", movement.Notes);
        });

        var refill = await verify.PickListRefills.Include(row => row.Lines).SingleAsync();
        Assert.Equal(Key, refill.IdempotencyKey);
        Assert.Equal(MachineA, refill.MachineId);
        Assert.Equal(RecordedAt, refill.RecordedAtUtc);
        Assert.Equal(
            [.. movements.Select(movement => movement.Id)],
            refill.Lines.OrderBy(line => line.Id).Select(line => line.StockAdjustmentId));
    }

    /// <summary>
    /// A refill is costing-neutral: it is an internal transfer from storage to a machine, so costing
    /// quantity, inventory value and the average unit cost are exactly what the opening restock left,
    /// and no COGS exists (AGENTS.md § Inventory and historical costing invariants).
    /// </summary>
    [Fact]
    public async Task A_refill_leaves_costing_quantity_inventory_value_and_cogs_untouched()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedProductAsync(options, BusinessA, 100, quantity: 10, unitCost: 2m);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            await UseCaseFor(db).Handle(Input(rows: [(100, 4)]), CancellationToken.None);
        }

        await using var verify = TestAppDbContext.For(options, BusinessA);
        var product = await verify.Products.SingleAsync(p => p.Id == 100);

        Assert.Equal(6, product.QuantityInStock);
        Assert.Equal(10, product.CostingQuantity);
        Assert.Equal(20m, product.InventoryValue);
        Assert.Equal(2m, product.AverageUnitCost);

        var movement = await verify.StockAdjustments
            .SingleAsync(adjustment => adjustment.Reason == StockAdjustmentReason.MachineRefill);
        Assert.Equal(10, movement.CostingQuantityAfter);
        Assert.Equal(20m, movement.InventoryValueAfter);
    }

    [Fact]
    public async Task A_refill_with_no_machine_is_recorded_with_no_machine()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedProductAsync(options, BusinessA, 100, quantity: 10);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var attempt = await UseCaseFor(db).Handle(Input(rows: [(100, 2)]), CancellationToken.None);
            Assert.Equal(PickListRefillOutcome.Recorded, attempt.Outcome);
            Assert.Null(attempt.Record!.MachineId);
        }

        await using var verify = TestAppDbContext.For(options, BusinessA);
        Assert.Null((await verify.PickListRefills.SingleAsync()).MachineId);
        Assert.Null((await verify.StockAdjustments
            .SingleAsync(adjustment => adjustment.Reason == StockAdjustmentReason.MachineRefill)).MachineId);
    }

    #endregion

    #region Refusals leave nothing behind

    /// <summary>
    /// The transaction, proved: the first row is applicable and the second is not, so the first
    /// row's movement and the quantity it had already written are rolled back with the batch.
    /// </summary>
    [Fact]
    public async Task One_row_above_storage_stock_leaves_every_other_row_unwritten()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedProductAsync(options, BusinessA, 100, quantity: 10);
        await SeedProductAsync(options, BusinessA, 101, quantity: 1);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var attempt = await UseCaseFor(db).Handle(
                Input(rows: [(100, 3), (101, 4)]), CancellationToken.None);

            Assert.Equal(PickListRefillOutcome.Rejected, attempt.Outcome);
            var refusal = Assert.Single(attempt.Rejection!.Rows);
            Assert.Equal(1, refusal.RowIndex);
            Assert.Equal(PickListRefillPolicy.NotEnoughStockMessage(1), refusal.Message);
        }

        await using var verify = TestAppDbContext.For(options, BusinessA);
        Assert.Equal(10, (await verify.Products.SingleAsync(p => p.Id == 100)).QuantityInStock);
        Assert.Equal(1, (await verify.Products.SingleAsync(p => p.Id == 101)).QuantityInStock);
        Assert.Empty(verify.StockAdjustments
            .Where(adjustment => adjustment.Reason == StockAdjustmentReason.MachineRefill));
        Assert.Empty(verify.PickListRefills);
    }

    [Fact]
    public async Task A_duplicate_row_refuses_the_batch_and_writes_nothing()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedProductAsync(options, BusinessA, 100, quantity: 10);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var attempt = await UseCaseFor(db).Handle(
                Input(rows: [(100, 1), (100, 2)]), CancellationToken.None);

            Assert.Equal(PickListRefillOutcome.Rejected, attempt.Outcome);
            Assert.Equal(
                PickListRefillPolicy.DuplicateProductMessage,
                Assert.Single(attempt.Rejection!.Rows).Message);
        }

        await using var verify = TestAppDbContext.For(options, BusinessA);
        Assert.Equal(10, (await verify.Products.SingleAsync(p => p.Id == 100)).QuantityInStock);
        Assert.Empty(verify.PickListRefills);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task A_quantity_that_is_not_greater_than_zero_refuses_the_batch(int quantity)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedProductAsync(options, BusinessA, 100, quantity: 10);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var attempt = await UseCaseFor(db).Handle(Input(rows: [(100, quantity)]), CancellationToken.None);

            Assert.Equal(PickListRefillOutcome.Rejected, attempt.Outcome);
            Assert.Equal(
                PickListRefillPolicy.QuantityMustBePositiveMessage,
                Assert.Single(attempt.Rejection!.Rows).Message);
        }

        await using var verify = TestAppDbContext.For(options, BusinessA);
        Assert.Equal(10, (await verify.Products.SingleAsync(p => p.Id == 100)).QuantityInStock);
    }

    [Fact]
    public async Task A_retired_product_refuses_the_batch()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedProductAsync(options, BusinessA, 100, quantity: 10, isActive: false);

        await using var db = TestAppDbContext.For(options, BusinessA);
        var attempt = await UseCaseFor(db).Handle(Input(rows: [(100, 1)]), CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Rejected, attempt.Outcome);
        Assert.Equal(
            PickListRefillPolicy.RetiredProductMessage,
            Assert.Single(attempt.Rejection!.Rows).Message);
    }

    [Fact]
    public async Task A_deactivated_machine_refuses_the_batch()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedProductAsync(options, BusinessA, 100, quantity: 10);
        await SeedMachineAsync(options, BusinessA, MachineA, isActive: false);

        await using var db = TestAppDbContext.For(options, BusinessA);
        var attempt = await UseCaseFor(db).Handle(
            Input(machineId: MachineA, rows: [(100, 1)]), CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Rejected, attempt.Outcome);
        Assert.Equal(
            PickListRefillPolicy.MachineMustBeActiveMessage,
            Assert.Single(attempt.Rejection!.Request).Message);
    }

    #endregion

    #region Retrying one submission

    [Fact]
    public async Task The_same_key_and_the_same_request_returns_the_original_and_records_nothing_new()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedProductAsync(options, BusinessA, 100, quantity: 10);
        await SeedProductAsync(options, BusinessA, 101, quantity: 10);
        await SeedMachineAsync(options, BusinessA, MachineA);

        var input = Input(machineId: MachineA, notes: "Friday run", rows: [(100, 3), (101, 2)]);

        PickListRefillRecord first;
        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            first = (await UseCaseFor(db).Handle(input, CancellationToken.None)).Record!;
        }

        // A separate context, the way a retried HTTP request arrives.
        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var retry = await UseCaseFor(db).Handle(input, CancellationToken.None);

            Assert.Equal(PickListRefillOutcome.Replayed, retry.Outcome);
            AssertSameRefill(first, retry.Record!);
        }

        await using var verify = TestAppDbContext.For(options, BusinessA);
        Assert.Equal(7, (await verify.Products.SingleAsync(p => p.Id == 100)).QuantityInStock);
        Assert.Equal(8, (await verify.Products.SingleAsync(p => p.Id == 101)).QuantityInStock);
        Assert.Equal(
            2,
            await verify.StockAdjustments.CountAsync(a => a.Reason == StockAdjustmentReason.MachineRefill));
        Assert.Single(verify.PickListRefills);
    }

    [Fact]
    public async Task The_same_key_with_a_different_request_is_a_conflict_and_records_nothing_new()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedProductAsync(options, BusinessA, 100, quantity: 10);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            await UseCaseFor(db).Handle(Input(rows: [(100, 3)]), CancellationToken.None);
        }

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var conflict = await Assert.ThrowsAsync<DomainConflictException>(
                () => UseCaseFor(db).Handle(Input(rows: [(100, 4)]), CancellationToken.None));

            Assert.Equal(PickListRefillPolicy.IdempotencyKeyReusedMessage, conflict.Message);
        }

        await using var verify = TestAppDbContext.For(options, BusinessA);
        Assert.Equal(7, (await verify.Products.SingleAsync(p => p.Id == 100)).QuantityInStock);
        Assert.Single(verify.PickListRefills);
    }

    /// <summary>
    /// A retried submission is answered with the quantities the original recorded, not with what
    /// the shelf holds now - which is why the row's submitted quantity is stored on it.
    /// </summary>
    [Fact]
    public async Task A_retry_answers_the_original_rows_after_other_movements()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedProductAsync(options, BusinessA, 100, quantity: 10);

        var input = Input(rows: [(100, 3)]);

        PickListRefillRecord first;
        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            first = (await UseCaseFor(db).Handle(input, CancellationToken.None)).Record!;
        }

        // Somebody else picks from the same shelf in between.
        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            await UseCaseFor(db).Handle(
                Input(key: "a-second-submission", rows: [(100, 2)]), CancellationToken.None);
        }

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var retry = await UseCaseFor(db).Handle(input, CancellationToken.None);
            AssertSameRefill(first, retry.Record!);
        }

        await using var verify = TestAppDbContext.For(options, BusinessA);
        Assert.Equal(5, (await verify.Products.SingleAsync(p => p.Id == 100)).QuantityInStock);
    }

    #endregion

    #region The tenant boundary

    /// <summary>
    /// A key is unique per business, not globally: two businesses picking on the same day may
    /// legitimately choose the same string, and neither may see or replay the other's refill
    /// (AGENTS.md § Tenant ownership and data isolation).
    /// </summary>
    [Fact]
    public async Task Two_businesses_may_use_the_same_idempotency_key()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedProductAsync(options, BusinessA, 100, quantity: 10);
        await SeedProductAsync(options, BusinessB, 200, quantity: 10);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var attempt = await UseCaseFor(db).Handle(Input(rows: [(100, 3)]), CancellationToken.None);
            Assert.Equal(PickListRefillOutcome.Recorded, attempt.Outcome);
        }

        await using (var db = TestAppDbContext.For(options, BusinessB))
        {
            // The same key, a different business: recorded, not replayed and not refused.
            var attempt = await UseCaseFor(db).Handle(Input(rows: [(200, 5)]), CancellationToken.None);
            Assert.Equal(PickListRefillOutcome.Recorded, attempt.Outcome);
        }

        await using var verify = TestAppDbContext.Unrestricted(options);
        var refills = await verify.PickListRefills.OrderBy(row => row.BusinessId).ToListAsync();
        Assert.Equal([BusinessA, BusinessB], refills.Select(refill => refill.BusinessId));
        Assert.All(refills, refill => Assert.Equal(Key, refill.IdempotencyKey));
    }

    [Fact]
    public async Task Another_businesss_product_is_refused_as_an_unknown_product()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedProductAsync(options, BusinessB, 200, quantity: 99);

        await using var db = TestAppDbContext.For(options, BusinessA);
        var attempt = await UseCaseFor(db).Handle(Input(rows: [(200, 1)]), CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Rejected, attempt.Outcome);
        Assert.Equal(
            PickListRefillPolicy.UnknownProductMessage,
            Assert.Single(attempt.Rejection!.Rows).Message);
    }

    [Fact]
    public async Task Another_businesss_machine_is_refused()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedProductAsync(options, BusinessA, 100, quantity: 10);
        await SeedMachineAsync(options, BusinessB, MachineB);

        await using var db = TestAppDbContext.For(options, BusinessA);
        var attempt = await UseCaseFor(db).Handle(
            Input(machineId: MachineB, rows: [(100, 1)]), CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Rejected, attempt.Outcome);
        Assert.Equal(
            PickListRefillPolicy.MachineMustBeActiveMessage,
            Assert.Single(attempt.Rejection!.Request).Message);
    }

    /// <summary>
    /// The ownership stamp, on both new tables: a recorded refill and its rows belong to the
    /// business the request resolved, and nothing in the request chose it.
    /// </summary>
    [Fact]
    public async Task The_recorded_refill_and_its_rows_are_stamped_with_the_callers_business()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedProductAsync(options, BusinessA, 100, quantity: 10);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            await UseCaseFor(db).Handle(Input(rows: [(100, 1)]), CancellationToken.None);
        }

        await using var verify = TestAppDbContext.Unrestricted(options);
        Assert.Equal(BusinessA, (await verify.PickListRefills.SingleAsync()).BusinessId);
        Assert.Equal(BusinessA, (await verify.PickListRefillLines.SingleAsync()).BusinessId);
    }

    /// <summary>
    /// A refill from the pick list is indistinguishable, in the audit trail, from one entered
    /// product by product: same reason, same source. That is what keeps the machine-stock ledger and
    /// Sync Restock's duplicate handling working without learning about this table.
    /// </summary>
    [Fact]
    public async Task The_movements_are_the_established_manual_machine_refill()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedProductAsync(options, BusinessA, 100, quantity: 10);
        await SeedMachineAsync(options, BusinessA, MachineA);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            await UseCaseFor(db).Handle(Input(machineId: MachineA, rows: [(100, 2)]), CancellationToken.None);
        }

        await using var verify = TestAppDbContext.For(options, BusinessA);
        var movement = await verify.StockAdjustments.SingleAsync(a => a.QuantityChange == -2);

        Assert.Equal(DomainStock.StockAdjustmentReason.MachineRefill, (DomainStock.StockAdjustmentReason)movement.Reason);
        Assert.Equal(DomainStock.StockAdjustmentSource.Manual, (DomainStock.StockAdjustmentSource)movement.Source);
        Assert.Equal(8, movement.QuantityAfter);
        Assert.Null(movement.EatBefore);
        Assert.Null(movement.ReceiptItemId);
    }

    #endregion
}
