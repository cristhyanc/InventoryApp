using Inventory.Application.Machines;
using Inventory.Application.MachineStockSync;
using Inventory.Domain.Nayax;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Application.MachineStockSync;

/// <summary>
/// A product batch whose <em>commit</em> fails (issue #1127, repair of PR #1132).
///
/// <para>A failed commit is the same failure as a movement that cannot be recorded or a rebuild that
/// hits fatal history: nothing the batch staged was committed. So it must be answered the same way -
/// every event whose write was staged reports the established <c>Error</c> with no movement id, the
/// non-writing outcomes already decided keep theirs, the transaction's disposal rolls the writes back,
/// and the next product is still applied. A cancelled request is the one exception and still stops the
/// whole apply.</para>
///
/// <para>The commit is failed by a decorator over the real store, so the reads, the staging, the
/// rebuild and the rollback are the production ones; only the final <c>COMMIT</c> is replaced by a
/// provider error, and the transaction is then disposed uncommitted exactly as it would be.</para>
/// </summary>
public class MachineStockSyncCommitFailureTests
{
    private const int SellerBusinessId = 1;
    private const long MachineId = 900;
    private const long LocalMachineId = 31;

    private static readonly MachineIdentity Machine = new(LocalMachineId, MachineId);
    private static readonly DateTime EventTime = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);

    private const long Coke = 200;
    private const long Water = 201;

    private const string FailedMessage = "Failed to apply this event; no inventory movement was recorded.";

    [Fact]
    public async Task A_failed_product_commit_reports_its_staged_events_as_errors_and_the_next_product_still_applies()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteDbAsync(connection);
        await using var db = TestAppDbContext.For(options, SellerBusinessId);

        SeedCostedProduct(db, Coke, "Coke 375mL", 10);
        SeedCostedProduct(db, Water, "Nu Pure Spring Water 600mL", 10);
        SeedEvent(db, 1, Coke, 4);
        SeedEvent(db, 2, Water, 3);
        SeedEvent(db, 3, Coke, 4);
        // Decided against the 2 the first two Coke refills leave, before the commit fails.
        SeedEvent(db, 4, Coke, 5);
        await db.SaveChangesAsync();

        var ids = await EventIdsAsync(db);
        var store = FailsCommitFor.Product(Coke, Store(db));

        var result = await new ApplyMachineStockSync(store).Handle(Machine, ids, CancellationToken.None);

        Assert.True(store.FailedACommit, "The Coke commit was never attempted, so nothing was proved.");
        Assert.Equal(ids, result.Results.Select(r => r.EventId));

        var cokeStaged = new[] { result.Results[0], result.Results[2] };
        Assert.All(cokeStaged, failed =>
        {
            Assert.Equal(NayaxStockEventApplyOutcome.Error, failed.Outcome);
            Assert.Equal(FailedMessage, failed.Message);
            Assert.Null(failed.StockAdjustmentId);
        });

        // A non-writing outcome keeps what it was decided as.
        Assert.Equal(NayaxStockEventApplyOutcome.InsufficientStock, result.Results[3].Outcome);
        Assert.Null(result.Results[3].StockAdjustmentId);

        // The product after the failed one was still processed and committed.
        Assert.Equal(NayaxStockEventApplyOutcome.Applied, result.Results[1].Outcome);
        Assert.NotNull(result.Results[1].StockAdjustmentId);

        await using (var verify = TestAppDbContext.For(options, SellerBusinessId))
        {
            Assert.Equal(10, (await verify.Products.FindAsync(Coke))!.QuantityInStock);
            Assert.Equal(7, (await verify.Products.FindAsync(Water))!.QuantityInStock);

            var refill = Assert.Single(await verify.StockAdjustments
                .Where(x => x.Reason == StockAdjustmentReason.MachineRefill)
                .ToListAsync());
            Assert.Equal(Water, refill.ProductId);
            Assert.Equal(result.Results[1].StockAdjustmentId, refill.Id);

            Assert.Equal(
                [
                    NayaxStockEventProcessingStatus.Unprocessed,
                    NayaxStockEventProcessingStatus.Applied,
                    NayaxStockEventProcessingStatus.Unprocessed,
                    NayaxStockEventProcessingStatus.Unprocessed,
                ],
                await verify.NayaxMachineStockEvents.OrderBy(e => e.Id).Select(e => e.ProcessingStatus).ToListAsync());
        }

        // Nothing about the Coke events is settled, so a retry applies them.
        var retried = await new ApplyMachineStockSync(Store(db))
            .Handle(Machine, [ids[0], ids[2]], CancellationToken.None);
        Assert.All(retried.Results, r => Assert.Equal(NayaxStockEventApplyOutcome.Applied, r.Outcome));

        await using var reread = TestAppDbContext.For(options, SellerBusinessId);
        Assert.Equal(2, (await reread.Products.FindAsync(Coke))!.QuantityInStock);
    }

    [Fact]
    public async Task A_cancelled_request_whose_commit_fails_still_stops_the_whole_apply()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteDbAsync(connection);
        await using var db = TestAppDbContext.For(options, SellerBusinessId);

        SeedCostedProduct(db, Coke, "Coke 375mL", 10);
        SeedCostedProduct(db, Water, "Nu Pure Spring Water 600mL", 10);
        SeedEvent(db, 1, Coke, 4);
        SeedEvent(db, 2, Water, 3);
        await db.SaveChangesAsync();

        var ids = await EventIdsAsync(db);
        using var cancellation = new CancellationTokenSource();
        var store = FailsCommitFor.Product(Coke, Store(db), cancelling: cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new ApplyMachineStockSync(store).Handle(Machine, ids, cancellation.Token));

        await using var verify = TestAppDbContext.For(options, SellerBusinessId);
        Assert.Empty(await verify.StockAdjustments.Where(x => x.Reason == StockAdjustmentReason.MachineRefill).ToListAsync());
        Assert.Equal(10, (await verify.Products.FindAsync(Water))!.QuantityInStock);
    }

    [Fact]
    public async Task A_failed_commit_of_apply_as_separate_restock_reports_the_error_and_leaves_the_event_unresolved()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteDbAsync(connection);
        await using var db = TestAppDbContext.For(options, SellerBusinessId);

        SeedCostedProduct(db, Coke, "Coke 375mL", 10);
        SeedEvent(db, 1, Coke, 4);
        SeedMatchingManualRefill(db, Coke, 4);
        await db.SaveChangesAsync();

        var eventId = (await EventIdsAsync(db)).Single();
        var store = FailsCommitFor.Product(Coke, Store(db));

        var result = await new ResolveMachineStockDuplicate(store).Handle(
            Machine, eventId, NayaxDuplicateResolutionChoice.ApplyAsSeparateRestock, CancellationToken.None);

        Assert.True(store.FailedACommit, "The override's commit was never attempted, so nothing was proved.");
        Assert.Equal(NayaxStockEventApplyOutcome.Error, result.Outcome);
        Assert.Equal(FailedMessage, result.Message);
        Assert.Null(result.StockAdjustmentId);

        await using (var verify = TestAppDbContext.For(options, SellerBusinessId))
        {
            Assert.Equal(10, (await verify.Products.FindAsync(Coke))!.QuantityInStock);
            Assert.Empty(await verify.StockAdjustments
                .Where(x => x.Reason == StockAdjustmentReason.MachineRefill && x.Source == StockAdjustmentSource.Nayax)
                .ToListAsync());

            var stored = await verify.NayaxMachineStockEvents.SingleAsync();
            Assert.Equal(NayaxStockEventProcessingStatus.Unprocessed, stored.ProcessingStatus);
            Assert.Equal(NayaxDuplicateResolution.None, stored.DuplicateResolution);
            Assert.Null(stored.StockAdjustmentId);
        }

        // Still flagged and unresolved, so the operator can simply choose the override again.
        var retried = await new ResolveMachineStockDuplicate(Store(db)).Handle(
            Machine, eventId, NayaxDuplicateResolutionChoice.ApplyAsSeparateRestock, CancellationToken.None);
        Assert.Equal(NayaxStockEventApplyOutcome.Applied, retried.Outcome);
        Assert.NotNull(retried.StockAdjustmentId);
    }

    private static EfMachineStockEventStore Store(AppDbContext db) =>
        new(db, TestCostingUseCases.RecordMovement(db), TestCostingUseCases.Rebuild(db));

    private static async Task<List<int>> EventIdsAsync(AppDbContext db) =>
        await db.NayaxMachineStockEvents.AsNoTracking()
            .OrderBy(e => e.Id)
            .Select(e => e.Id)
            .ToListAsync();

    private static void SeedEvent(AppDbContext db, long nayaxEventLogId, long productId, int quantity) =>
        db.NayaxMachineStockEvents.Add(new NayaxMachineStockEvent
        {
            NayaxEventLogId = nayaxEventLogId,
            MachineId = MachineId,
            EventCode = NayaxMachineAlertEventCodes.StockAdjustForMachine,
            EventDateTimeGmt = EventTime,
            EventDateTimeVmc = EventTime.AddHours(10),
            RawEventData = $"Product MDB: 7 | Product {productId} | {quantity}",
            ParsedMdb = 7,
            ParsedProductName = $"Product {productId}",
            ParsedQuantity = quantity,
            MatchedProductId = productId,
            MatchStatus = NayaxStockEventMatchStatus.Matched,
        });

    /// <summary>A product whose stored stock agrees with its history: one costed opening restock.</summary>
    private static void SeedCostedProduct(AppDbContext db, long id, string name, int quantityInStock)
    {
        db.Products.Add(new Product
        {
            Id = id,
            ExternalProductId = NayaxExternalId.From(id),
            Name = name,
            QuantityInStock = quantityInStock,
            CostingQuantity = quantityInStock,
            InventoryValue = quantityInStock,
            AverageUnitCost = 1m,
        });

        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = id,
            QuantityChange = quantityInStock,
            QuantityAfter = quantityInStock,
            Reason = StockAdjustmentReason.Restock,
            UnitCost = 1m,
            EffectiveAt = EventTime.AddDays(-2),
        });
    }

    /// <summary>
    /// A manual refill of the same product, quantity and time, which is what flags the event as a
    /// possible duplicate the "Apply as separate restock" override may resolve (issue #196). Seeded as
    /// a movement row only; storage is not this test's subject.
    /// </summary>
    private static void SeedMatchingManualRefill(AppDbContext db, long productId, int quantity) =>
        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = productId,
            QuantityChange = -quantity,
            QuantityAfter = 10,
            Reason = StockAdjustmentReason.MachineRefill,
            Source = StockAdjustmentSource.Manual,
            MachineId = LocalMachineId,
            EffectiveAt = EventTime,
        });

    private static async Task<DbContextOptions<AppDbContext>> CreateSqliteDbAsync(SqliteConnection connection)
    {
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using (var schema = TestAppDbContext.Unrestricted(options))
        {
            await schema.Database.EnsureCreatedAsync();
        }

        await using var setup = TestAppDbContext.Unrestricted(options);
        setup.Businesses.Add(new Business
        {
            Id = SellerBusinessId,
            Name = "Vending A",
            Status = BusinessStatus.Active,
            SalesProvider = ExternalProvider.Nayax,
            CreatedAtUtc = EventTime,
        });
        setup.Machines.Add(new LocalMachine
        {
            BusinessId = SellerBusinessId,
            Id = LocalMachineId,
            Name = "Machine 900",
            ExternalMachineId = NayaxExternalId.From(MachineId),
        });
        await setup.SaveChangesAsync();
        return options;
    }

    /// <summary>
    /// The real store, except that the commit of the transaction that applied
    /// <c>failingProductId</c>'s refills fails with a provider error (or, when <c>cancelling</c> is
    /// given, with the request being cancelled). The transaction is then left uncommitted, so its
    /// disposal rolls back exactly as it would after a real failed commit.
    /// </summary>
    private sealed class FailsCommitFor : IMachineStockEventStore
    {
        private readonly long _failingProductId;
        private readonly IMachineStockEventStore _inner;
        private readonly CancellationTokenSource? _cancelling;
        private long? _lastAppliedProductId;

        private FailsCommitFor(long failingProductId, IMachineStockEventStore inner, CancellationTokenSource? cancelling)
        {
            _failingProductId = failingProductId;
            _inner = inner;
            _cancelling = cancelling;
        }

        public static FailsCommitFor Product(
            long failingProductId, IMachineStockEventStore inner, CancellationTokenSource? cancelling = null) =>
            new(failingProductId, inner, cancelling);

        public bool FailedACommit { get; private set; }

        public async Task<IMachineStockRefillWriteTransaction> BeginRefillWriteTransactionAsync(
            CancellationToken cancellationToken)
        {
            _lastAppliedProductId = null;
            return new Transaction(this, await _inner.BeginRefillWriteTransactionAsync(cancellationToken));
        }

        public Task<MachineRefillApplication> ApplyProductRefillsAsync(
            MachineIdentity machine,
            long productId,
            IReadOnlyList<MachineStockRefillRequest> refills,
            CancellationToken cancellationToken)
        {
            _lastAppliedProductId = productId;
            return _inner.ApplyProductRefillsAsync(machine, productId, refills, cancellationToken);
        }

        public Task<IReadOnlyList<long>> GetImportedNayaxEventLogIdsAsync(long nayaxMachineId, CancellationToken cancellationToken) =>
            _inner.GetImportedNayaxEventLogIdsAsync(nayaxMachineId, cancellationToken);

        public Task<IReadOnlyDictionary<long, NayaxStockSyncProduct>> GetStorageProductsAsync(
            IReadOnlyCollection<long> productIds, CancellationToken cancellationToken) =>
            _inner.GetStorageProductsAsync(productIds, cancellationToken);

        public Task ImportAsync(IReadOnlyList<MachineStockEventImport> events, CancellationToken cancellationToken) =>
            _inner.ImportAsync(events, cancellationToken);

        public Task<MachineStockEventsPage> GetUnprocessedEventsAsync(
            long nayaxMachineId,
            CancellationToken cancellationToken,
            DateTime? fromDateGmt = null,
            bool includeReconciled = false) =>
            _inner.GetUnprocessedEventsAsync(nayaxMachineId, cancellationToken, fromDateGmt, includeReconciled);

        public Task<IReadOnlyList<ManualRefillEvidence>> GetManualMachineRefillsAsync(
            long localMachineId, CancellationToken cancellationToken) =>
            _inner.GetManualMachineRefillsAsync(localMachineId, cancellationToken);

        public Task<MachineStockEventState?> FindEventAsync(long nayaxMachineId, int eventId, CancellationToken cancellationToken) =>
            _inner.FindEventAsync(nayaxMachineId, eventId, cancellationToken);

        public Task<IReadOnlyList<MachineStockEventState>> FindEventsAsync(
            long nayaxMachineId, IReadOnlyCollection<int> eventIds, CancellationToken cancellationToken) =>
            _inner.FindEventsAsync(nayaxMachineId, eventIds, cancellationToken);

        public Task<NayaxStockSyncProduct?> FindStorageProductAsync(long productId, CancellationToken cancellationToken) =>
            _inner.FindStorageProductAsync(productId, cancellationToken);

        public Task<bool> ReconcileAsManualDuplicateAsync(
            int eventId, long nayaxMachineId, int? matchedManualStockAdjustmentId, CancellationToken cancellationToken) =>
            _inner.ReconcileAsManualDuplicateAsync(eventId, nayaxMachineId, matchedManualStockAdjustmentId, cancellationToken);

        private sealed class Transaction(FailsCommitFor owner, IMachineStockRefillWriteTransaction inner)
            : IMachineStockRefillWriteTransaction
        {
            public Task CommitAsync(CancellationToken cancellationToken)
            {
                if (owner._lastAppliedProductId != owner._failingProductId)
                    return inner.CommitAsync(cancellationToken);

                owner.FailedACommit = true;
                if (owner._cancelling is { } cancelling)
                {
                    cancelling.Cancel();
                    throw new OperationCanceledException(cancelling.Token);
                }

                throw new SqliteException("SQLite Error 5: 'database is locked'.", 5);
            }

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
