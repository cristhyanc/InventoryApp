using Inventory.Application.Machines;
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
/// The multi-machine refill batch end to end over a real relational database (issue #897): the whole
/// use case on <see cref="EfMultiMachinePickListRefillStore"/>, the Pick List's own storage read and
/// the real machines adapter, over SQLite.
///
/// <para>Relational rather than InMemory, deliberately. What this slice is about is a transaction:
/// every allocation commits together or none does, the key's uniqueness is the database's rule as much
/// as the use case's, the per-allocation machine is a real foreign key, and a batch leaves ordinary
/// <c>StockAdjustment</c> rows whose costing columns are the established <c>MachineRefill</c> ones.
/// None of that is provable without real SQL (AGENTS.md § Database and migrations). The
/// <c>BEGIN IMMEDIATE</c> write lock needs two genuine writers and is in
/// <see cref="MultiMachinePickListRefillConcurrencyTests"/>.</para>
/// </summary>
public class EfMultiMachinePickListRefillStoreTests
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;
    private const long MachineA = 40;
    private const long MachineB = 41;
    private const long MachineOfB = 42;

    private static readonly DateTime RecordedAt = new(2026, 10, 25, 2, 30, 0, DateTimeKind.Utc);

    private const string Key = "b9e2d1c0-7a6b-4c5d-8e9f-0a1b2c3d4e5f";

    private static async Task<DbContextOptions<AppDbContext>> CreateSqliteAsync(SqliteConnection connection)
    {
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var setup = TestAppDbContext.Unrestricted(options);
        await setup.Database.EnsureCreatedAsync();
        return options;
    }

    private static RecordMultiMachinePickListRefill UseCaseFor(AppDbContext db) =>
        new(
            new EfMultiMachinePickListRefillStore(
                db, TestCostingUseCases.RecordMovement(db), TestCostingUseCases.Rebuild(db)),
            new EfPickListStorageStockStore(db),
            new EfLocalMachineStore(db),
            new FakeClock(RecordedAt));

    /// <summary>The single-machine batch of issue #743, over the same tables and the same key column.</summary>
    private static RecordPickListRefill SingleMachineUseCaseFor(AppDbContext db) =>
        new(
            new EfPickListRefillStore(
                db, TestCostingUseCases.RecordMovement(db), TestCostingUseCases.Rebuild(db)),
            new EfPickListStorageStockStore(db),
            new EfLocalMachineStore(db),
            new FakeClock(RecordedAt));

    private static MultiMachinePickListRefillInput Input(
        string? key = Key,
        string? notes = null,
        params (long? MachineId, long ProductId, int Quantity)[] allocations) =>
        new(
            key,
            notes,
            [.. allocations.Select(allocation => new MultiMachineRefillAllocation(
                allocation.MachineId, allocation.ProductId, allocation.Quantity))]);

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

    private static async Task SeedMachineAsync(
        DbContextOptions<AppDbContext> options, int businessId, long machineId, bool isActive = true)
    {
        await using var seed = TestAppDbContext.For(options, businessId);
        seed.Machines.Add(new LocalMachine { Id = machineId, Name = $"Machine {machineId}", IsActive = isActive });
        await seed.SaveChangesAsync();
    }

    /// <summary>
    /// Two answers describe the same recorded batch. Compared field by field because the record's own
    /// equality compares its allocation list by reference, and a replay reads its allocations back from
    /// the database rather than handing back the instance the original built.
    /// </summary>
    private static void AssertSameRefill(
        MultiMachinePickListRefillRecord expected, MultiMachinePickListRefillRecord actual)
    {
        Assert.Equal(expected.IdempotencyKey, actual.IdempotencyKey);
        Assert.Equal(expected.Notes, actual.Notes);
        Assert.Equal(expected.RecordedAtUtc, actual.RecordedAtUtc);
        Assert.Equal(expected.Allocations, actual.Allocations);
    }

    #region Recording the batch

    /// <summary>
    /// The criterion itself: one product allocated to two machines in one request records one
    /// <c>MachineRefill</c> movement per allocation, each attributed to its own machine, and storage
    /// falls by exactly the sum.
    /// </summary>
    [Fact]
    public async Task One_product_across_two_machines_records_one_movement_per_allocation()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedProductAsync(options, BusinessA, 100, quantity: 10);
        await SeedMachineAsync(options, BusinessA, MachineA);
        await SeedMachineAsync(options, BusinessA, MachineB);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var attempt = await UseCaseFor(db).Handle(
                Input(notes: "Friday run", allocations: [(MachineA, 100, 6), (MachineB, 100, 4)]),
                CancellationToken.None);

            Assert.Equal(PickListRefillOutcome.Recorded, attempt.Outcome);
            Assert.Equal(RecordedAt, attempt.Record!.RecordedAtUtc);
            Assert.Equal(
                [(MachineA, 100L, 6), (MachineB, 100L, 4)],
                attempt.Record.Allocations.Select(
                    allocation => (allocation.MachineId, allocation.ProductId, allocation.QuantityTaken)));
        }

        await using var verify = TestAppDbContext.For(options, BusinessA);

        // Storage down by the sum of the allocations, and by nothing else.
        Assert.Equal(0, (await verify.Products.SingleAsync(p => p.Id == 100)).QuantityInStock);

        var movements = await verify.StockAdjustments
            .Where(adjustment => adjustment.Reason == StockAdjustmentReason.MachineRefill)
            .OrderBy(adjustment => adjustment.Id)
            .ToListAsync();

        Assert.Equal([-6, -4], movements.Select(movement => movement.QuantityChange));
        Assert.Equal([MachineA, MachineB], movements.Select(movement => movement.MachineId));
        Assert.All(movements, movement =>
        {
            Assert.Equal(StockAdjustmentSource.Manual, movement.Source);
            Assert.Equal("Friday run", movement.Notes);
        });

        // The batch row carries no machine; each line carries its own, and the lines point at the
        // movements they recorded.
        var refill = await verify.PickListRefills.Include(row => row.Lines).SingleAsync();
        Assert.Equal(Key, refill.IdempotencyKey);
        Assert.Null(refill.MachineId);
        Assert.Equal(
            [(MachineA, 100L, 6), (MachineB, 100L, 4)],
            refill.Lines.OrderBy(line => line.Id)
                .Select(line => (line.MachineId, line.ProductId, line.QuantityTaken)));
        Assert.Equal(
            [.. movements.Select(movement => movement.Id)],
            refill.Lines.OrderBy(line => line.Id).Select(line => line.StockAdjustmentId));
    }

    [Fact]
    public async Task Different_products_on_different_machines_are_each_recorded()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedProductAsync(options, BusinessA, 100, quantity: 10);
        await SeedProductAsync(options, BusinessA, 101, quantity: 5);
        await SeedMachineAsync(options, BusinessA, MachineA);
        await SeedMachineAsync(options, BusinessA, MachineB);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var attempt = await UseCaseFor(db).Handle(
                Input(allocations: [(MachineA, 100, 3), (MachineB, 101, 5), (MachineB, 100, 1)]),
                CancellationToken.None);

            Assert.Equal(PickListRefillOutcome.Recorded, attempt.Outcome);
        }

        await using var verify = TestAppDbContext.For(options, BusinessA);
        Assert.Equal(6, (await verify.Products.SingleAsync(p => p.Id == 100)).QuantityInStock);
        Assert.Equal(0, (await verify.Products.SingleAsync(p => p.Id == 101)).QuantityInStock);
        Assert.Equal(
            3,
            await verify.StockAdjustments.CountAsync(a => a.Reason == StockAdjustmentReason.MachineRefill));
    }

    /// <summary>
    /// A refill is costing-neutral, including when the same product is taken to two machines in one
    /// request: it is an internal transfer from storage to a machine, so costing quantity, inventory
    /// value and the average unit cost are exactly what the opening restock left, and no COGS exists
    /// (AGENTS.md § Inventory and historical costing invariants).
    /// </summary>
    [Fact]
    public async Task A_batch_leaves_costing_quantity_inventory_value_and_cogs_untouched()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedProductAsync(options, BusinessA, 100, quantity: 10, unitCost: 2m);
        await SeedMachineAsync(options, BusinessA, MachineA);
        await SeedMachineAsync(options, BusinessA, MachineB);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            await UseCaseFor(db).Handle(
                Input(allocations: [(MachineA, 100, 4), (MachineB, 100, 3)]), CancellationToken.None);
        }

        await using var verify = TestAppDbContext.For(options, BusinessA);
        var product = await verify.Products.SingleAsync(p => p.Id == 100);

        Assert.Equal(3, product.QuantityInStock);
        Assert.Equal(10, product.CostingQuantity);
        Assert.Equal(20m, product.InventoryValue);
        Assert.Equal(2m, product.AverageUnitCost);

        var movements = await verify.StockAdjustments
            .Where(adjustment => adjustment.Reason == StockAdjustmentReason.MachineRefill)
            .ToListAsync();
        // Both allocations leave the costing position exactly where the opening restock left it: a
        // refill consumes no business-owned inventory, so its running position is unchanged and it
        // creates no COGS. (A refill movement may still carry the product's average unit cost as
        // information, which is the established StockMovementCostPolicy behaviour for any outgoing
        // movement; the cost replay deliberately ignores it for a MachineRefill, which is what makes
        // the transfer costing-neutral.)
        Assert.Equal(2, movements.Count);
        Assert.All(movements, movement =>
        {
            Assert.Equal(10, movement.CostingQuantityAfter);
            Assert.Equal(20m, movement.InventoryValueAfter);
            Assert.Equal(2m, movement.AverageUnitCostAfter);
        });
    }

    /// <summary>
    /// A batch records exactly one movement per allocation and nothing else: no second machine-stock
    /// movement, which is what keeps the machine-stock ledger a derivation of these facts rather than
    /// a second ledger (AGENTS.md § Inventory and historical costing invariants).
    /// </summary>
    [Fact]
    public async Task The_movements_are_the_established_manual_machine_refill_and_there_are_no_others()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedProductAsync(options, BusinessA, 100, quantity: 10);
        await SeedMachineAsync(options, BusinessA, MachineA);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            await UseCaseFor(db).Handle(
                Input(allocations: [(MachineA, 100, 2)]), CancellationToken.None);
        }

        await using var verify = TestAppDbContext.For(options, BusinessA);
        var movement = await verify.StockAdjustments.SingleAsync(a => a.QuantityChange == -2);

        Assert.Equal(
            DomainStock.StockAdjustmentReason.MachineRefill,
            (DomainStock.StockAdjustmentReason)movement.Reason);
        Assert.Equal(
            DomainStock.StockAdjustmentSource.Manual, (DomainStock.StockAdjustmentSource)movement.Source);
        Assert.Equal(MachineA, movement.MachineId);
        Assert.Equal(8, movement.QuantityAfter);
        Assert.Null(movement.EatBefore);
        Assert.Null(movement.ReceiptItemId);

        // The opening restock and this one movement: nothing recorded a machine-stock event of its own.
        Assert.Equal(2, await verify.StockAdjustments.CountAsync());
        Assert.Empty(verify.NayaxMachineStockEvents);
        Assert.Empty(verify.MachineStockCounts);
    }

    #endregion

    #region Refusals leave nothing behind

    /// <summary>
    /// The acceptance criterion with the shelf in it: storage 10, allocations of 6 and 6. The first
    /// allocation's movement and the quantity it had already written are rolled back with the batch,
    /// so the refusal leaves the storeroom exactly as it was.
    /// </summary>
    [Fact]
    public async Task Allocations_that_together_exceed_storage_leave_nothing_written()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedProductAsync(options, BusinessA, 100, quantity: 10);
        await SeedMachineAsync(options, BusinessA, MachineA);
        await SeedMachineAsync(options, BusinessA, MachineB);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var attempt = await UseCaseFor(db).Handle(
                Input(allocations: [(MachineA, 100, 6), (MachineB, 100, 6)]), CancellationToken.None);

            Assert.Equal(PickListRefillOutcome.Rejected, attempt.Outcome);
            Assert.Equal([0, 1], attempt.Rejection!.Allocations.Select(r => r.AllocationIndex));
            Assert.All(
                attempt.Rejection.Allocations,
                refusal => Assert.Equal(
                    MultiMachinePickListRefillPolicy.NotEnoughStockForTotalMessage(12, 10), refusal.Message));
        }

        await using var verify = TestAppDbContext.For(options, BusinessA);
        Assert.Equal(10, (await verify.Products.SingleAsync(p => p.Id == 100)).QuantityInStock);
        Assert.Empty(verify.StockAdjustments
            .Where(adjustment => adjustment.Reason == StockAdjustmentReason.MachineRefill));
        Assert.Empty(verify.PickListRefills);
        Assert.Empty(verify.PickListRefillLines);
    }

    /// <summary>
    /// The same refusal when the product's total is too large for a 32-bit sum (the regression for
    /// the overflow reported on PR #913). Each quantity is a positive whole number the shape rules
    /// accept, and adding them up in an <see cref="int"/> wraps to a value below the shelf - which
    /// would have let the one rule this endpoint exists for pass and written the batch. Over a real
    /// database, with the storage quantity, the movements, the refill, its lines and the idempotency
    /// key all proved untouched: the key is still unspent afterwards, which is what shows the refusal
    /// claimed nothing.
    /// </summary>
    [Fact]
    public async Task Allocations_whose_total_exceeds_a_32_bit_sum_leave_nothing_written()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedProductAsync(options, BusinessA, 100, quantity: 10);
        await SeedMachineAsync(options, BusinessA, MachineA);
        await SeedMachineAsync(options, BusinessA, MachineB);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var attempt = await UseCaseFor(db).Handle(
                Input(allocations: [(MachineA, 100, int.MaxValue), (MachineB, 100, int.MaxValue)]),
                CancellationToken.None);

            Assert.Equal(PickListRefillOutcome.Rejected, attempt.Outcome);
            Assert.Equal([0, 1], attempt.Rejection!.Allocations.Select(r => r.AllocationIndex));
            Assert.All(
                attempt.Rejection.Allocations,
                refusal => Assert.Equal(
                    MultiMachinePickListRefillPolicy.NotEnoughStockForTotalMessage(2L * int.MaxValue, 10),
                    refusal.Message));
        }

        await using (var verify = TestAppDbContext.For(options, BusinessA))
        {
            Assert.Equal(10, (await verify.Products.SingleAsync(p => p.Id == 100)).QuantityInStock);
            Assert.Empty(verify.StockAdjustments
                .Where(adjustment => adjustment.Reason == StockAdjustmentReason.MachineRefill));
            Assert.Empty(verify.PickListRefills);
            Assert.Empty(verify.PickListRefillLines);
        }

        // The key the refused batch carried is still that business's to spend, because nothing about
        // the refusal was recorded.
        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var attempt = await UseCaseFor(db).Handle(
                Input(allocations: [(MachineA, 100, 4)]), CancellationToken.None);

            Assert.Equal(PickListRefillOutcome.Recorded, attempt.Outcome);
        }

        await using var after = TestAppDbContext.For(options, BusinessA);
        Assert.Equal(6, (await after.Products.SingleAsync(p => p.Id == 100)).QuantityInStock);
        Assert.Equal(Key, (await after.PickListRefills.SingleAsync()).IdempotencyKey);
    }

    /// <summary>
    /// A machine a recorded allocation names cannot be deleted, and it is <em>this</em> column that
    /// says so (issue #896's deletion rule, merged as PR #912, integrating with issue #897's new
    /// <c>PickListRefillLines.MachineId</c>).
    ///
    /// <para>The point of the test is the isolation. In an ordinary batch every allocation also
    /// leaves a <c>StockAdjustment</c> carrying the same machine, so a reference count that never
    /// looked at a refill line would still block the delete - by accident, through a coupling nothing
    /// asserts. Here the line's movement deliberately names no machine (the persisted shape a
    /// no-machine refill already produces), so the <strong>only</strong> row naming the machine is the
    /// allocation, and the composite <c>(BusinessId, MachineId)</c> restricted foreign key is proved
    /// to be what refuses the delete. It is checked with a raw <c>DELETE</c> so the answer is the
    /// database's own and not whatever EF has in its change tracker.</para>
    ///
    /// <para>This is the database's backstop, not an operator-facing refusal: the counted,
    /// message-bearing refusal lives in <c>Inventory.Domain.Machines.MachineDeletionPolicy</c>, whose
    /// reference count must learn about this column too - see the closing note in
    /// <c>docs/architecture.md</c> § Multi-machine refill batch.</para>
    /// </summary>
    [Fact]
    public async Task A_machine_a_recorded_allocation_names_cannot_be_deleted_by_its_own_reference_alone()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedProductAsync(options, BusinessA, 100, quantity: 10);
        await SeedMachineAsync(options, BusinessA, MachineA);

        await using (var seed = TestAppDbContext.For(options, BusinessA))
        {
            // A refill movement that names no machine, which is exactly what a no-machine refill
            // records, so nothing but the allocation below refers to MachineA.
            var movement = new StockAdjustment
            {
                ProductId = 100,
                QuantityChange = -3,
                QuantityAfter = 7,
                Reason = StockAdjustmentReason.MachineRefill,
                Source = StockAdjustmentSource.Manual,
                MachineId = null,
                EffectiveAt = RecordedAt,
            };
            seed.StockAdjustments.Add(movement);
            await seed.SaveChangesAsync();

            seed.PickListRefills.Add(new PickListRefill
            {
                IdempotencyKey = Key,
                RequestFingerprint = new string('f', 64),
                MachineId = null,
                RecordedAtUtc = RecordedAt,
                Lines =
                [
                    new PickListRefillLine
                    {
                        MachineId = MachineA,
                        ProductId = 100,
                        QuantityTaken = 3,
                        StockAdjustmentId = movement.Id,
                    },
                ],
            });
            await seed.SaveChangesAsync();
        }

        await using var db = TestAppDbContext.For(options, BusinessA);
        Assert.Empty(db.StockAdjustments.Where(movement => movement.MachineId == MachineA));
        Assert.Empty(db.PickListRefills.Where(refill => refill.MachineId == MachineA));

        var failure = await Assert.ThrowsAsync<SqliteException>(
            () => db.Database.ExecuteSqlRawAsync("DELETE FROM Machines WHERE Id = {0};", MachineA));
        Assert.Contains("FOREIGN KEY constraint failed", failure.Message, StringComparison.OrdinalIgnoreCase);

        // Nothing was deleted and nothing was reassigned or nulled: the allocation still names the
        // machine it was walked to.
        Assert.NotNull(await db.Machines.SingleOrDefaultAsync(machine => machine.Id == MachineA));
        Assert.Equal(MachineA, (await db.PickListRefillLines.SingleAsync()).MachineId);
    }

    /// <summary>
    /// The transaction, proved on the ordinary case too: the first allocation is applicable and a
    /// later one is not, so everything the batch had staged is discarded.
    /// </summary>
    [Fact]
    public async Task One_unapplicable_allocation_leaves_every_other_allocation_unwritten()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedProductAsync(options, BusinessA, 100, quantity: 10);
        await SeedProductAsync(options, BusinessA, 101, quantity: 1);
        await SeedMachineAsync(options, BusinessA, MachineA);
        await SeedMachineAsync(options, BusinessA, MachineB);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var attempt = await UseCaseFor(db).Handle(
                Input(allocations: [(MachineA, 100, 3), (MachineB, 101, 4)]), CancellationToken.None);

            Assert.Equal(PickListRefillOutcome.Rejected, attempt.Outcome);
            var refusal = Assert.Single(attempt.Rejection!.Allocations);
            Assert.Equal(1, refusal.AllocationIndex);
        }

        await using var verify = TestAppDbContext.For(options, BusinessA);
        Assert.Equal(10, (await verify.Products.SingleAsync(p => p.Id == 100)).QuantityInStock);
        Assert.Equal(1, (await verify.Products.SingleAsync(p => p.Id == 101)).QuantityInStock);
        Assert.Empty(verify.StockAdjustments
            .Where(adjustment => adjustment.Reason == StockAdjustmentReason.MachineRefill));
        Assert.Empty(verify.PickListRefills);
    }

    [Fact]
    public async Task A_deactivated_machine_refuses_the_batch_and_writes_nothing()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedProductAsync(options, BusinessA, 100, quantity: 10);
        await SeedMachineAsync(options, BusinessA, MachineA);
        await SeedMachineAsync(options, BusinessA, MachineB, isActive: false);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var attempt = await UseCaseFor(db).Handle(
                Input(allocations: [(MachineA, 100, 1), (MachineB, 100, 1)]), CancellationToken.None);

            Assert.Equal(PickListRefillOutcome.Rejected, attempt.Outcome);
            var refusal = Assert.Single(attempt.Rejection!.Allocations);
            Assert.Equal(1, refusal.AllocationIndex);
            Assert.Equal(PickListRefillPolicy.MachineMustBeActiveMessage, refusal.Message);
        }

        await using var verify = TestAppDbContext.For(options, BusinessA);
        Assert.Equal(10, (await verify.Products.SingleAsync(p => p.Id == 100)).QuantityInStock);
        Assert.Empty(verify.PickListRefills);
    }

    [Fact]
    public async Task A_retired_product_refuses_the_batch()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedProductAsync(options, BusinessA, 100, quantity: 10, isActive: false);
        await SeedMachineAsync(options, BusinessA, MachineA);

        await using var db = TestAppDbContext.For(options, BusinessA);
        var attempt = await UseCaseFor(db).Handle(
            Input(allocations: [(MachineA, 100, 1)]), CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Rejected, attempt.Outcome);
        Assert.Equal(
            PickListRefillPolicy.RetiredProductMessage,
            Assert.Single(attempt.Rejection!.Allocations).Message);
    }

    /// <summary>
    /// A machine deactivated between the operator's read and this write is refused, because the fleet
    /// is read inside the transaction the batch is applied in rather than before it. The race itself is
    /// in <see cref="MultiMachinePickListRefillConcurrencyTests"/>; this is the sequential proof that
    /// the read is current and not a cached answer.
    /// </summary>
    [Fact]
    public async Task A_machine_deactivated_after_the_operator_read_it_refuses_the_batch()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedProductAsync(options, BusinessA, 100, quantity: 10);
        await SeedMachineAsync(options, BusinessA, MachineA);

        await using (var deactivate = TestAppDbContext.For(options, BusinessA))
        {
            var attempt = await new EfLocalMachineStore(deactivate)
                .SetActiveAsync(MachineA, isActive: false, CancellationToken.None);
            Assert.Equal(LocalMachineWriteOutcome.Succeeded, attempt.Outcome);
        }

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var attempt = await UseCaseFor(db).Handle(
                Input(allocations: [(MachineA, 100, 1)]), CancellationToken.None);

            Assert.Equal(PickListRefillOutcome.Rejected, attempt.Outcome);
            Assert.Equal(
                PickListRefillPolicy.MachineMustBeActiveMessage,
                Assert.Single(attempt.Rejection!.Allocations).Message);
        }

        await using var verify = TestAppDbContext.For(options, BusinessA);
        Assert.Equal(10, (await verify.Products.SingleAsync(p => p.Id == 100)).QuantityInStock);
        Assert.Empty(verify.PickListRefills);
    }

    #endregion

    #region Retrying one submission

    [Fact]
    public async Task The_same_key_and_the_same_batch_returns_the_original_and_records_nothing_new()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedProductAsync(options, BusinessA, 100, quantity: 10);
        await SeedMachineAsync(options, BusinessA, MachineA);
        await SeedMachineAsync(options, BusinessA, MachineB);

        var input = Input(notes: "Friday run", allocations: [(MachineA, 100, 3), (MachineB, 100, 2)]);

        MultiMachinePickListRefillRecord first;
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
        Assert.Equal(5, (await verify.Products.SingleAsync(p => p.Id == 100)).QuantityInStock);
        Assert.Equal(
            2,
            await verify.StockAdjustments.CountAsync(a => a.Reason == StockAdjustmentReason.MachineRefill));
        Assert.Single(verify.PickListRefills);
    }

    /// <summary>
    /// The criterion's order-independence over the real store: the retry's allocations arrive in
    /// another order, and it is still the same submission - answered with the original allocations, in
    /// the original order, with the original movement ids.
    /// </summary>
    [Fact]
    public async Task A_retry_with_reordered_allocations_replays_the_original()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedProductAsync(options, BusinessA, 100, quantity: 10);
        await SeedProductAsync(options, BusinessA, 101, quantity: 10);
        await SeedMachineAsync(options, BusinessA, MachineA);
        await SeedMachineAsync(options, BusinessA, MachineB);

        MultiMachinePickListRefillRecord first;
        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            first = (await UseCaseFor(db).Handle(
                Input(allocations: [(MachineA, 100, 3), (MachineB, 101, 2)]), CancellationToken.None))
                .Record!;
        }

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var retry = await UseCaseFor(db).Handle(
                Input(allocations: [(MachineB, 101, 2), (MachineA, 100, 3)]), CancellationToken.None);

            Assert.Equal(PickListRefillOutcome.Replayed, retry.Outcome);
            AssertSameRefill(first, retry.Record!);
        }

        await using var verify = TestAppDbContext.For(options, BusinessA);
        Assert.Equal(7, (await verify.Products.SingleAsync(p => p.Id == 100)).QuantityInStock);
        Assert.Equal(8, (await verify.Products.SingleAsync(p => p.Id == 101)).QuantityInStock);
        Assert.Single(verify.PickListRefills);
    }

    [Fact]
    public async Task The_same_key_with_different_allocations_is_a_conflict_and_records_nothing_new()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedProductAsync(options, BusinessA, 100, quantity: 10);
        await SeedMachineAsync(options, BusinessA, MachineA);
        await SeedMachineAsync(options, BusinessA, MachineB);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            await UseCaseFor(db).Handle(
                Input(allocations: [(MachineA, 100, 3)]), CancellationToken.None);
        }

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var conflict = await Assert.ThrowsAsync<DomainConflictException>(
                () => UseCaseFor(db).Handle(
                    Input(allocations: [(MachineB, 100, 3)]), CancellationToken.None));

            Assert.Equal(PickListRefillPolicy.IdempotencyKeyReusedMessage, conflict.Message);
        }

        await using var verify = TestAppDbContext.For(options, BusinessA);
        Assert.Equal(7, (await verify.Products.SingleAsync(p => p.Id == 100)).QuantityInStock);
        Assert.Single(verify.PickListRefills);
    }

    #endregion

    #region The two refill contracts share one key namespace

    /// <summary>
    /// A key the single-machine endpoint already spent is refused as reused, and the refill it recorded
    /// is left exactly as it is - never reshaped into a batch and never replayed as one.
    /// </summary>
    [Fact]
    public async Task A_key_that_recorded_a_single_machine_refill_is_a_conflict_here()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedProductAsync(options, BusinessA, 100, quantity: 10);
        await SeedMachineAsync(options, BusinessA, MachineA);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var attempt = await SingleMachineUseCaseFor(db).Handle(
                new PickListRefillInput(Key, MachineA, null, [new PickListRefillRequestRow(100, 3)]),
                CancellationToken.None);
            Assert.Equal(PickListRefillOutcome.Recorded, attempt.Outcome);
        }

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var conflict = await Assert.ThrowsAsync<DomainConflictException>(
                () => UseCaseFor(db).Handle(
                    Input(allocations: [(MachineA, 100, 3)]), CancellationToken.None));

            Assert.Equal(PickListRefillPolicy.IdempotencyKeyReusedMessage, conflict.Message);
        }

        await using var verify = TestAppDbContext.For(options, BusinessA);

        // The single-machine refill is untouched: its machine is still on the batch row, its line
        // still has none, and nothing new was recorded.
        var refill = await verify.PickListRefills.Include(row => row.Lines).SingleAsync();
        Assert.Equal(MachineA, refill.MachineId);
        Assert.Null(Assert.Single(refill.Lines).MachineId);
        Assert.Equal(7, (await verify.Products.SingleAsync(p => p.Id == 100)).QuantityInStock);
    }

    /// <summary>
    /// And the other way round: a key this endpoint spent is refused by the single-machine endpoint
    /// rather than replayed there, so neither contract can be answered with the other's refill.
    /// </summary>
    [Fact]
    public async Task A_key_that_recorded_a_batch_is_a_conflict_on_the_single_machine_endpoint()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedProductAsync(options, BusinessA, 100, quantity: 10);
        await SeedMachineAsync(options, BusinessA, MachineA);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var attempt = await UseCaseFor(db).Handle(
                Input(allocations: [(MachineA, 100, 3)]), CancellationToken.None);
            Assert.Equal(PickListRefillOutcome.Recorded, attempt.Outcome);
        }

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var conflict = await Assert.ThrowsAsync<DomainConflictException>(
                () => SingleMachineUseCaseFor(db).Handle(
                    new PickListRefillInput(Key, MachineA, null, [new PickListRefillRequestRow(100, 3)]),
                    CancellationToken.None));

            Assert.Equal(PickListRefillPolicy.IdempotencyKeyReusedMessage, conflict.Message);
        }

        await using var verify = TestAppDbContext.For(options, BusinessA);
        Assert.Equal(7, (await verify.Products.SingleAsync(p => p.Id == 100)).QuantityInStock);
        Assert.Single(verify.PickListRefills);
    }

    /// <summary>
    /// The single-machine endpoint keeps working beside this one, under its own key: its request, its
    /// response and its stored shape are unchanged by the added column.
    /// </summary>
    [Fact]
    public async Task The_single_machine_endpoint_still_records_a_refill_of_its_own_shape()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedProductAsync(options, BusinessA, 100, quantity: 10);
        await SeedMachineAsync(options, BusinessA, MachineA);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var attempt = await SingleMachineUseCaseFor(db).Handle(
                new PickListRefillInput(
                    "a-single-machine-submission", MachineA, null, [new PickListRefillRequestRow(100, 2)]),
                CancellationToken.None);

            Assert.Equal(PickListRefillOutcome.Recorded, attempt.Outcome);
            Assert.Equal(MachineA, attempt.Record!.MachineId);
        }

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var attempt = await UseCaseFor(db).Handle(
                Input(allocations: [(MachineA, 100, 3)]), CancellationToken.None);
            Assert.Equal(PickListRefillOutcome.Recorded, attempt.Outcome);
        }

        await using var verify = TestAppDbContext.For(options, BusinessA);
        Assert.Equal(5, (await verify.Products.SingleAsync(p => p.Id == 100)).QuantityInStock);

        var refills = await verify.PickListRefills
            .Include(row => row.Lines)
            .OrderBy(row => row.Id)
            .ToListAsync();
        Assert.Equal([MachineA, null], refills.Select(refill => refill.MachineId));
        Assert.Equal([null, MachineA], refills.Select(refill => Assert.Single(refill.Lines).MachineId));
    }

    #endregion

    #region The tenant boundary

    /// <summary>
    /// A key is unique per business, not globally: two businesses picking on the same day may
    /// legitimately choose the same string, and neither may see or replay the other's batch (AGENTS.md
    /// § Tenant ownership and data isolation).
    /// </summary>
    [Fact]
    public async Task Two_businesses_may_use_the_same_idempotency_key()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedProductAsync(options, BusinessA, 100, quantity: 10);
        await SeedMachineAsync(options, BusinessA, MachineA);
        await SeedProductAsync(options, BusinessB, 200, quantity: 10);
        await SeedMachineAsync(options, BusinessB, MachineOfB);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var attempt = await UseCaseFor(db).Handle(
                Input(allocations: [(MachineA, 100, 3)]), CancellationToken.None);
            Assert.Equal(PickListRefillOutcome.Recorded, attempt.Outcome);
        }

        await using (var db = TestAppDbContext.For(options, BusinessB))
        {
            // The same key, a different business: recorded, not replayed and not refused.
            var attempt = await UseCaseFor(db).Handle(
                Input(allocations: [(MachineOfB, 200, 5)]), CancellationToken.None);
            Assert.Equal(PickListRefillOutcome.Recorded, attempt.Outcome);
        }

        await using var verify = TestAppDbContext.Unrestricted(options);
        var refills = await verify.PickListRefills.OrderBy(row => row.BusinessId).ToListAsync();
        Assert.Equal([BusinessA, BusinessB], refills.Select(refill => refill.BusinessId));
        Assert.All(refills, refill => Assert.Equal(Key, refill.IdempotencyKey));
    }

    [Fact]
    public async Task Another_businesss_machine_is_refused()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedProductAsync(options, BusinessA, 100, quantity: 10);
        await SeedMachineAsync(options, BusinessA, MachineA);
        await SeedMachineAsync(options, BusinessB, MachineOfB);

        await using var db = TestAppDbContext.For(options, BusinessA);
        var attempt = await UseCaseFor(db).Handle(
            Input(allocations: [(MachineOfB, 100, 1)]), CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Rejected, attempt.Outcome);
        Assert.Equal(
            PickListRefillPolicy.MachineMustBeActiveMessage,
            Assert.Single(attempt.Rejection!.Allocations).Message);
    }

    [Fact]
    public async Task Another_businesss_product_is_refused_as_an_unknown_product()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedMachineAsync(options, BusinessA, MachineA);
        await SeedProductAsync(options, BusinessB, 200, quantity: 99);

        await using var db = TestAppDbContext.For(options, BusinessA);
        var attempt = await UseCaseFor(db).Handle(
            Input(allocations: [(MachineA, 200, 1)]), CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Rejected, attempt.Outcome);
        Assert.Equal(
            PickListRefillPolicy.UnknownProductMessage,
            Assert.Single(attempt.Rejection!.Allocations).Message);
    }

    /// <summary>
    /// The ownership stamp, on the batch and on every line: a recorded batch and its allocations belong
    /// to the business the request resolved, and nothing in the request chose it.
    /// </summary>
    [Fact]
    public async Task The_recorded_batch_and_its_allocations_are_stamped_with_the_callers_business()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedProductAsync(options, BusinessA, 100, quantity: 10);
        await SeedMachineAsync(options, BusinessA, MachineA);
        await SeedMachineAsync(options, BusinessA, MachineB);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            await UseCaseFor(db).Handle(
                Input(allocations: [(MachineA, 100, 1), (MachineB, 100, 1)]), CancellationToken.None);
        }

        await using var verify = TestAppDbContext.Unrestricted(options);
        Assert.Equal(BusinessA, (await verify.PickListRefills.SingleAsync()).BusinessId);
        Assert.Equal(
            [BusinessA, BusinessA],
            await verify.PickListRefillLines.Select(line => line.BusinessId).ToListAsync());
    }

    #endregion
}
