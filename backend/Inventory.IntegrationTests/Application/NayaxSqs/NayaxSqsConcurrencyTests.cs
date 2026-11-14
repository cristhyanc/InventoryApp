using Inventory.Application.Costing;
using Inventory.Application.Nayax;
using Inventory.Application.NayaxSqs;
using Inventory.Application.Purchases;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Nayax;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.NayaxSqs;

/// <summary>
/// Issue #1103's required concurrency and contention cases, over a real SQLite <strong>file</strong>
/// with two connections - which is the only way two writers and SQLite's own locking exist at all.
///
/// <para><strong>The defect these are about.</strong> The store read "is this transaction stored?" and
/// then inserted, outside any transaction. An SQS message and a <c>lastSales</c> import of the same
/// transaction could therefore both read "not stored", and the loser's whole <c>SaveChanges</c> was
/// refused by the unique index on <c>(BusinessId, TransactionID)</c> - a Dashboard refresh failing
/// over a sale the queue had just stored, or the other way round. Both paths now open the same
/// <c>BEGIN IMMEDIATE</c> transaction, so the second writer waits and then reads the first one's
/// committed state, which turns the race into the enrich path instead of a lock error.</para>
/// </summary>
public class NayaxSqsConcurrencyTests
{
    /// <summary>
    /// <strong>The required concurrency case.</strong> A queued message and a <c>lastSales</c> import
    /// of the same transaction, started together against one file: the result is exactly one sale row
    /// and exactly one costing-inventory consumption, the loser takes the enrich path, and neither
    /// side fails.
    /// </summary>
    [Fact]
    public async Task An_sqs_message_and_a_lastSales_import_of_one_transaction_produce_one_sale()
    {
        await using var fixture = await NayaxSqsSaleFixture.OnDiskAsync();

        await using var first = fixture.Context();
        await using var second = fixture.SecondWriter();

        var queue = NayaxSqsSaleFixture.Store(first);
        var sync = NayaxSqsSaleFixture.Store(second);

        // Started together on two connections. Which one takes the write lock first is genuinely up
        // to SQLite; what is asserted is that the outcome does not depend on the answer.
        var both = await Task.WhenAll(
            Task.Run(() => queue.PersistSqsSaleAsync(
                NayaxSqsSaleFixture.SqsSale(NayaxTransactionStatusIds.Completed),
                CancellationToken.None)),
            Task.Run(async () =>
            {
                await sync.PersistLatestSalesAsync(
                    [NayaxSqsSaleFixture.LastSalesItem()], CancellationToken.None);

                // The shapes differ, so the lastSales half answers the queue half's record type with
                // the one fact this case compares: it did not throw.
                return new Inventory.Application.SalesSync.NayaxSqsSalePersistResult(false, []);
            }));

        Assert.Equal(2, both.Length);

        // Exactly one row for the transaction, whichever side inserted it.
        await using (var drain = fixture.Context())
        {
            Assert.Single(await drain.NayaxSales.AsNoTracking().ToListAsync());

            await NayaxSqsSaleFixture.Store(drain).RebuildInventoryCostsAsync(CancellationToken.None);
        }

        await using var read = fixture.Context();
        var sale = await read.NayaxSales.AsNoTracking().SingleAsync();
        Assert.Equal(NayaxTransactionStatusIds.Completed, sale.TransactionStatusId);

        // And exactly one unit consumed: the enrich path finds the status already decided, so nothing
        // is counted twice.
        var product = await read.Products.AsNoTracking()
            .SingleAsync(row => row.Id == NayaxSqsSaleFixture.ProductA);
        Assert.Equal(9, product.CostingQuantity);
        Assert.Equal(18m, product.InventoryValue);
    }

    /// <summary>
    /// Two queued messages for the same transaction, started together - the shape a standard queue
    /// produces on its own, because it can deliver one message more than once. One row, one
    /// consumption, and the second message reports the transaction as already stored.
    /// </summary>
    [Fact]
    public async Task Two_deliveries_of_one_transaction_at_once_produce_one_sale()
    {
        await using var fixture = await NayaxSqsSaleFixture.OnDiskAsync();

        await using var first = fixture.Context();
        await using var second = fixture.SecondWriter();

        var results = await Task.WhenAll(
            Task.Run(() => NayaxSqsSaleFixture.Store(first).PersistSqsSaleAsync(
                NayaxSqsSaleFixture.SqsSale(NayaxTransactionStatusIds.Completed, messageId: "m1"),
                CancellationToken.None)),
            Task.Run(() => NayaxSqsSaleFixture.Store(second).PersistSqsSaleAsync(
                NayaxSqsSaleFixture.SqsSale(NayaxTransactionStatusIds.Completed, messageId: "m2"),
                CancellationToken.None)));

        Assert.Single(results.Where(result => result.Inserted));
        Assert.Single(results.Where(result => !result.Inserted));

        await using (var drain = fixture.Context())
        {
            await NayaxSqsSaleFixture.Store(drain).RebuildInventoryCostsAsync(CancellationToken.None);
        }

        await using var read = fixture.Context();
        Assert.Single(await read.NayaxSales.AsNoTracking().ToListAsync());
        Assert.Equal(
            9,
            (await read.Products.AsNoTracking().SingleAsync(row => row.Id == NayaxSqsSaleFixture.ProductA))
                .CostingQuantity);
    }

    /// <summary>
    /// <strong>The required contention case.</strong> Another writer holds SQLite's write lock, so the
    /// reader's own <c>BEGIN IMMEDIATE</c> waits and then gives up at its busy timeout. That is
    /// classified <em>transient</em>: nothing was saved and the message is kept, with a bounded
    /// backoff, for the next receive.
    /// </summary>
    [Fact]
    public async Task A_write_lock_held_elsewhere_makes_the_message_transient_and_saves_nothing()
    {
        await using var fixture = await NayaxSqsSaleFixture.OnDiskAsync(busyTimeoutSeconds: 1);

        // The lock is taken from outside the application entirely: BEGIN IMMEDIATE plus a real write,
        // so SQLite is holding it rather than merely intending to.
        await using var blocker = fixture.RawConnection();
        await blocker.OpenAsync();
        await using var holding = await blocker.BeginTransactionAsync();
        await using (var write = blocker.CreateCommand())
        {
            write.CommandText =
                "UPDATE Products SET QuantityInStock = QuantityInStock WHERE Id = "
                + NayaxSqsSaleFixture.ProductA + ";";
            await write.ExecuteNonQueryAsync();
        }

        var outcome = await ProcessAsync(fixture);

        Assert.Equal(NayaxSqsDisposition.Transient, outcome.Disposition);
        Assert.Equal(NayaxSqsProcessingReason.StoreUnavailable, outcome.Reason);

        await holding.RollbackAsync();

        await using var read = fixture.Context();
        Assert.Empty(await read.NayaxSales.AsNoTracking().ToListAsync());
        Assert.Empty(await read.PendingInventoryCostRebuilds.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// The unique index's own refusal, recognised and recovered from - the defence in depth under the
    /// write lock. The lock makes this unreachable on one SQLite file, so the refusal is
    /// <em>injected</em> here: the other path has committed the transaction, and this save is told the
    /// index refused it. The store catches that, clears what it staged, and retries once as the
    /// enrich-only path, so a concurrent insert is never surfaced as a failure and never counted twice.
    /// </summary>
    [Fact]
    public async Task A_unique_index_refusal_is_retried_once_as_the_enrich_path()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();

        // The transaction the other path committed, with no status yet - which is what makes the
        // enrich branch do work, and therefore what makes the injected refusal land on a real save.
        await using (var other = fixture.Context())
        {
            other.NayaxSales.Add(new Inventory.Infrastructure.Models.NayaxSales
            {
                TransactionID = NayaxSqsSaleFixture.TransactionId,
                TransactionStatusId = null,
                MachineID = NayaxSqsSaleFixture.MachineId,
                SettlementValue = 3.5m,
                MachineAuthorizationTime = NayaxSqsSaleFixture.SaleInstant,
            });
            await other.SaveChangesAsync();
        }

        await using var db = fixture.Context();
        var refusing = new RefusingOnceCostSale(
            TestCostingUseCases.CostSale(db, TestCostingUseCases.Rebuild(db)));

        var store = new EfLatestNayaxSalesStore(
            db,
            refusing,
            TestCostingUseCases.Rebuild(db),
            new EfNayaxProductLinkStore(db),
            new FakeClock(NayaxSqsSaleFixture.ReceivedAtUtc));

        var persisted = await store.PersistSqsSaleAsync(
            NayaxSqsSaleFixture.SqsSale(NayaxTransactionStatusIds.CancelledOrDeclined26),
            CancellationToken.None);

        // Not surfaced as a failure, reported as already stored, and one row - never two.
        Assert.False(persisted.Inserted);
        Assert.Equal(2, refusing.Calls);

        await using var read = fixture.Context();
        var sale = await read.NayaxSales.AsNoTracking().SingleAsync();
        Assert.Equal(NayaxTransactionStatusIds.CancelledOrDeclined26, sale.TransactionStatusId);
    }

    #region The pending replay a drain is working on

    /// <summary>
    /// <strong>A sale recorded while a drain is replaying is never cleared by that drain.</strong>
    ///
    /// <para>The replay is deliberately outside the write lock - it reads and rewrites a product's
    /// whole history, and holding SQLite's one write lock for it would block every interactive
    /// request - so another writer can record an earlier sale for the same product and commit while
    /// it runs, which only ever moves that row's instant earlier. A drain that then removed the row
    /// by key would lose that earlier replay for good: the row said "replay from T1", only the span
    /// from T2 was replayed, and nothing would be left to say so.</para>
    ///
    /// <para>Two connections to one file, with the interleaving inside the replay itself, so the
    /// order is the one this is about rather than one the scheduler chose.</para>
    /// </summary>
    [Fact]
    public async Task A_sale_recorded_while_a_drain_replays_is_replayed_later_and_never_cleared()
    {
        await using var fixture = await NayaxSqsSaleFixture.OnDiskAsync();

        var later = NayaxSqsSaleFixture.SaleInstant;
        var earlier = NayaxSqsSaleFixture.SaleInstant.AddHours(-2);

        // The work the drain will read: one sale at T2, saved with its pending replay and not yet
        // replayed.
        await using (var seed = fixture.Context())
        {
            await NayaxSqsSaleFixture.Store(seed).PersistSqsSaleAsync(
                NayaxSqsSaleFixture.SqsSale(
                    NayaxTransactionStatusIds.Completed, transactionId: 4100000001, saleInstant: later),
                CancellationToken.None);
        }

        await using var drainContext = fixture.Context();
        await using var otherWriter = fixture.SecondWriter();

        var replay = new InterleavingReplay(
            drainContext,
            interleave: () => NayaxSqsSaleFixture.Store(otherWriter).PersistSqsSaleAsync(
                NayaxSqsSaleFixture.SqsSale(
                    NayaxTransactionStatusIds.Completed,
                    messageId: "message-2",
                    transactionId: 4100000002,
                    saleInstant: earlier),
                CancellationToken.None));

        var drained = await NayaxSqsSaleFixture.Store(drainContext, replay)
            .RebuildInventoryCostsAsync(CancellationToken.None);

        // The replay that ran is not the work that is pending now, so the product is reported as
        // still pending - because it is.
        Assert.Equal([NayaxSqsSaleFixture.ProductA], drained.StillPendingProductIds);
        Assert.Empty(drained.Failures);

        await using (var read = fixture.Context())
        {
            // The row is still there, at the earlier instant the other writer recorded.
            var pending = await read.PendingInventoryCostRebuilds.AsNoTracking().SingleAsync();
            Assert.Equal(earlier, pending.FromInstantUtc);

            // And nothing the abandoned replay staged was saved: costs computed before the earlier
            // sale existed must not stand as that product's position.
            Assert.NotEqual(
                InterleavingReplay.StaleCost,
                (await read.Products.AsNoTracking()
                    .SingleAsync(row => row.Id == NayaxSqsSaleFixture.ProductA)).AverageUnitCost);
        }

        // The next drain replays from the earlier instant and clears it: the work was preserved and
        // then done, which is the whole point of the row having survived.
        await using (var next = fixture.Context())
        {
            var again = await NayaxSqsSaleFixture.Store(next)
                .RebuildInventoryCostsAsync(CancellationToken.None);

            Assert.Empty(again.StillPendingProductIds);
            Assert.Empty(again.Failures);
        }

        await using var final = fixture.Context();
        Assert.Empty(await final.PendingInventoryCostRebuilds.AsNoTracking().ToListAsync());

        // Two completed sales, two units consumed, from the baseline's own opening position.
        var product = await final.Products.AsNoTracking()
            .SingleAsync(row => row.Id == NayaxSqsSaleFixture.ProductA);
        Assert.Equal(8, product.CostingQuantity);
        Assert.Equal(16m, product.InventoryValue);
    }

    /// <summary>
    /// <strong>A sale recorded at or after the instant a drain is replaying from is not cleared by
    /// that drain either.</strong>
    ///
    /// <para>This is the case the instant cannot answer. A second sale only ever moves
    /// <c>FromInstantUtc</c> <em>earlier</em>, so a sale at or after the instant the row already holds
    /// leaves that instant exactly as the drain read it - while the costing inputs the replay is
    /// computing from have changed underneath it. An acknowledgement that compared only the instant
    /// would therefore succeed: it would write costs computed without that sale in them and delete the
    /// work recorded for it, and the newer sale would never be replayed by anybody. What the
    /// acknowledgement compares is the row's revision, and every enqueue moves that whether the
    /// instant changes or not.</para>
    ///
    /// <para>The other writer commits <em>after</em> the drain has read its costing inputs and before
    /// it acknowledges, which is the window this is about. Two connections to one file, with the
    /// interleaving inside the replay itself.</para>
    /// </summary>
    /// <param name="hoursAfterTheReplayedInstant">
    /// <c>0</c> for a sale at exactly the instant being replayed from and <c>2</c> for a later one:
    /// both leave the row's instant where it is, which is what makes them one case.
    /// </param>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task A_sale_recorded_at_or_after_the_replayed_instant_is_not_cleared_by_that_drain(
        int hoursAfterTheReplayedInstant)
    {
        await using var fixture = await NayaxSqsSaleFixture.OnDiskAsync();

        var replayedFrom = NayaxSqsSaleFixture.SaleInstant;
        var second = replayedFrom.AddHours(hoursAfterTheReplayedInstant);

        // The work the drain will read: one sale, saved with its pending replay and not yet replayed.
        await using (var seed = fixture.Context())
        {
            await NayaxSqsSaleFixture.Store(seed).PersistSqsSaleAsync(
                NayaxSqsSaleFixture.SqsSale(
                    NayaxTransactionStatusIds.Completed,
                    transactionId: 4100000001,
                    saleInstant: replayedFrom),
                CancellationToken.None);
        }

        await using var drainContext = fixture.Context();
        await using var otherWriter = fixture.SecondWriter();

        var replay = new InterleavingReplay(
            drainContext,
            interleave: () => NayaxSqsSaleFixture.Store(otherWriter).PersistSqsSaleAsync(
                NayaxSqsSaleFixture.SqsSale(
                    NayaxTransactionStatusIds.Completed,
                    messageId: "message-2",
                    transactionId: 4100000002,
                    saleInstant: second),
                CancellationToken.None));

        var drained = await NayaxSqsSaleFixture.Store(drainContext, replay)
            .RebuildInventoryCostsAsync(CancellationToken.None);

        // The replay that ran is not the work that is pending now, so the product is reported as
        // still pending - because it is.
        Assert.Equal([NayaxSqsSaleFixture.ProductA], drained.StillPendingProductIds);
        Assert.Empty(drained.Failures);

        await using (var read = fixture.Context())
        {
            // The row survives, at the instant it already held - which is the whole difficulty: the
            // instant says nothing about the sale that arrived, and only the revision does.
            var pending = await read.PendingInventoryCostRebuilds.AsNoTracking().SingleAsync();
            Assert.Equal(replayedFrom, pending.FromInstantUtc);
            Assert.Equal(2, pending.Revision);

            // And nothing the superseded replay staged was saved: costs computed before the second
            // sale existed must not stand as that product's position.
            Assert.NotEqual(
                InterleavingReplay.StaleCost,
                (await read.Products.AsNoTracking()
                    .SingleAsync(row => row.Id == NayaxSqsSaleFixture.ProductA)).AverageUnitCost);
        }

        // The work is preserved and then done rather than lost: the next drain replays both sales and
        // clears the row.
        await using (var next = fixture.Context())
        {
            var again = await NayaxSqsSaleFixture.Store(next)
                .RebuildInventoryCostsAsync(CancellationToken.None);

            Assert.Empty(again.StillPendingProductIds);
            Assert.Empty(again.Failures);
        }

        await using var final = fixture.Context();
        Assert.Empty(await final.PendingInventoryCostRebuilds.AsNoTracking().ToListAsync());

        // Two completed sales, two units consumed, from the baseline's own opening position.
        var product = await final.Products.AsNoTracking()
            .SingleAsync(row => row.Id == NayaxSqsSaleFixture.ProductA);
        Assert.Equal(8, product.CostingQuantity);
        Assert.Equal(16m, product.InventoryValue);
    }

    /// <summary>
    /// <strong>Two drains of the same pending row leave one replay standing, and it is not the stale
    /// one.</strong> The queue reader and the request-driven synchronization both drain every pending
    /// row of the business, so two of them can hold the same work: one acknowledges it, and the other
    /// finishes a replay of work that is no longer pending. The second one's costs are discarded
    /// rather than written over the first one's, and it removes nothing it did not claim.
    /// </summary>
    [Fact]
    public async Task Two_drains_of_one_pending_row_keep_the_replay_that_acknowledged_it()
    {
        await using var fixture = await NayaxSqsSaleFixture.OnDiskAsync();

        await using (var seed = fixture.Context())
        {
            await NayaxSqsSaleFixture.Store(seed).PersistSqsSaleAsync(
                NayaxSqsSaleFixture.SqsSale(NayaxTransactionStatusIds.Completed),
                CancellationToken.None);
        }

        await using var firstContext = fixture.Context();
        await using var secondContext = fixture.SecondWriter();

        // The second drain runs to completion - the real replay, on its own connection - inside the
        // first one's replay, so the first one finishes holding work somebody else has acknowledged.
        var replay = new InterleavingReplay(
            firstContext,
            interleave: () => NayaxSqsSaleFixture.Store(secondContext)
                .RebuildInventoryCostsAsync(CancellationToken.None));

        var drained = await NayaxSqsSaleFixture.Store(firstContext, replay)
            .RebuildInventoryCostsAsync(CancellationToken.None);

        Assert.Equal([NayaxSqsSaleFixture.ProductA], drained.StillPendingProductIds);
        Assert.Empty(drained.Failures);

        await using var read = fixture.Context();

        // The row the other drain acknowledged is gone, and this one removed nothing of its own.
        Assert.Empty(await read.PendingInventoryCostRebuilds.AsNoTracking().ToListAsync());

        // What stands is the replay that acknowledged the work: one sale, one unit consumed, and not
        // a figure from the drain whose claim had gone.
        var product = await read.Products.AsNoTracking()
            .SingleAsync(row => row.Id == NayaxSqsSaleFixture.ProductA);
        Assert.Equal(9, product.CostingQuantity);
        Assert.Equal(18m, product.InventoryValue);
        Assert.Equal(2m, product.AverageUnitCost);
    }

    #endregion

    #region A stock write a drain is replaying over

    /// <summary>
    /// <strong>A purchase recorded while a drain is replaying is not overwritten by that drain.</strong>
    ///
    /// <para>This is the case the pending row's <c>Revision</c> cannot answer (issue #1245, finding
    /// INV-08 of audit #1222). Only a <em>sale</em> enqueues a pending rebuild, so a purchase committed
    /// while the replay is running adds no work to that row and leaves the drain's claim looking
    /// intact - while the costing inputs the replay is computing from have changed underneath it. The
    /// drain would then write a costing position with the purchase left out of it, exactly as the audit
    /// found: the purchase's own rebuild of 20 units worth $60 overwritten by the 9 units worth $18 the
    /// replay had computed before it existed. What the acknowledgement compares as well is the
    /// product's <c>CostingVersion</c>, which every cost rebuild moves.</para>
    ///
    /// <para>Two connections to one file, and the real replay with the other writer's purchase
    /// committed <em>after</em> it has read its costing inputs and before the drain acknowledges, which
    /// is the window a drain cannot hold the write lock across.</para>
    /// </summary>
    [Fact]
    public async Task A_purchase_recorded_while_a_drain_replays_is_not_overwritten_by_that_drain()
    {
        await using var fixture = await NayaxSqsSaleFixture.OnDiskAsync();

        // The work the drain will read: one completed sale, saved with its pending replay and not yet
        // replayed.
        await using (var seed = fixture.Context())
        {
            await NayaxSqsSaleFixture.Store(seed).PersistSqsSaleAsync(
                NayaxSqsSaleFixture.SqsSale(NayaxTransactionStatusIds.Completed),
                CancellationToken.None);
        }

        await using var drainContext = fixture.Context();
        await using var otherWriter = fixture.SecondWriter();

        var replay = InterleavedRealReplay(
            drainContext,
            interleave: () => RecordPurchaseAsync(otherWriter, unitCost: 4m));

        var drained = await NayaxSqsSaleFixture.Store(drainContext, replay)
            .RebuildInventoryCostsAsync(CancellationToken.None);

        // The replay that ran is not the history that is stored now, so the product is reported as
        // still pending - because it is.
        Assert.Equal([NayaxSqsSaleFixture.ProductA], drained.StillPendingProductIds);
        Assert.Empty(drained.Failures);

        await using (var read = fixture.Context())
        {
            // The row survives, so the product is rebuilt again and nothing is lost - and it survives
            // at the revision it already held, which is the whole difficulty: a purchase adds no work
            // to it, and only the product's costing version says the history moved.
            var pending = await read.PendingInventoryCostRebuilds.AsNoTracking().SingleAsync();
            Assert.Equal(NayaxSqsSaleFixture.SaleInstant, pending.FromInstantUtc);
            Assert.Equal(1, pending.Revision);

            // What stands is the purchase's own rebuild: the baseline's 10 units at $2, plus 10 at $4,
            // less the one unit the sale consumed at the $3 average it then had.
            Assert.Equal(
                (19, 57m, 3m),
                await CostingPositionAsync(read));
        }

        // The preserved work is then done: the next drain replays the sale over the history the
        // purchase left and clears the row, reaching the same position.
        await using (var next = fixture.Context())
        {
            var again = await NayaxSqsSaleFixture.Store(next)
                .RebuildInventoryCostsAsync(CancellationToken.None);

            Assert.Empty(again.StillPendingProductIds);
            Assert.Empty(again.Failures);
        }

        await using var final = fixture.Context();
        Assert.Empty(await final.PendingInventoryCostRebuilds.AsNoTracking().ToListAsync());
        Assert.Equal((19, 57m, 3m), await CostingPositionAsync(final));
    }

    /// <summary>
    /// <strong>A purchase <em>edit</em> committed while a drain is replaying is not overwritten
    /// either.</strong>
    ///
    /// <para>This is why the comparison is a version on the product rather than its latest movement
    /// (issue #1245): correcting a purchase line's quantity or unit cost rewrites the stored movement
    /// <em>in place</em> and adds no new one, so a latest-movement id would be exactly as it was while
    /// the product's whole cost history had changed. The edited unit cost is what separates the two
    /// outcomes here - both leave 19 units - so the figures say which rebuild stands.</para>
    /// </summary>
    [Fact]
    public async Task A_purchase_edit_committed_while_a_drain_replays_is_not_overwritten_by_that_drain()
    {
        await using var fixture = await NayaxSqsSaleFixture.OnDiskAsync();

        // A purchase the product's cost was already rebuilt over, and then the sale whose replay is
        // still pending.
        int purchaseId;
        await using (var seed = fixture.Context())
        {
            purchaseId = (await RecordPurchaseAsync(seed, unitCost: 4m)).Id;
        }

        await using (var seed = fixture.Context())
        {
            await NayaxSqsSaleFixture.Store(seed).PersistSqsSaleAsync(
                NayaxSqsSaleFixture.SqsSale(NayaxTransactionStatusIds.Completed),
                CancellationToken.None);
        }

        await using var drainContext = fixture.Context();
        await using var otherWriter = fixture.SecondWriter();

        var replay = InterleavedRealReplay(
            drainContext,
            interleave: () => EditPurchaseAsync(otherWriter, purchaseId, unitCost: 6m));

        var drained = await NayaxSqsSaleFixture.Store(drainContext, replay)
            .RebuildInventoryCostsAsync(CancellationToken.None);

        Assert.Equal([NayaxSqsSaleFixture.ProductA], drained.StillPendingProductIds);
        Assert.Empty(drained.Failures);

        await using (var read = fixture.Context())
        {
            var pending = await read.PendingInventoryCostRebuilds.AsNoTracking().SingleAsync();
            Assert.Equal(1, pending.Revision);

            // The edit changed the line in place, so there is still exactly one purchase movement -
            // and it carries the corrected cost.
            var movement = await read.StockAdjustments.AsNoTracking().SingleAsync();
            Assert.Equal(6m, movement.UnitCost);

            // What stands is the edit's own rebuild: 10 units at $2 plus 10 at the corrected $6, less
            // the one unit the sale consumed at the $4 average that leaves. The drain's own figures
            // would have been 19 units worth $57 at $3 - the same quantity, computed from the cost
            // before the correction.
            Assert.Equal((19, 76m, 4m), await CostingPositionAsync(read));
        }

        await using (var next = fixture.Context())
        {
            var again = await NayaxSqsSaleFixture.Store(next)
                .RebuildInventoryCostsAsync(CancellationToken.None);

            Assert.Empty(again.StillPendingProductIds);
            Assert.Empty(again.Failures);
        }

        await using var final = fixture.Context();
        Assert.Empty(await final.PendingInventoryCostRebuilds.AsNoTracking().ToListAsync());
        Assert.Equal((19, 76m, 4m), await CostingPositionAsync(final));
    }

    #endregion

    /// <summary>The purchase date the cases above buy on: after the baseline cutoff, before the sale.</summary>
    private static readonly DateTime PurchaseDate = new(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);

    private static EfPurchaseStore PurchaseStore(AppDbContext db) =>
        new(db, TestCostingUseCases.Rebuild(db), TestClocks.Fixed);

    private static PurchaseFields Fields() =>
        new("Fictional restock", null, null, null, null, PurchaseDate, null);

    private static Task<PurchaseRecord> RecordPurchaseAsync(AppDbContext db, decimal unitCost) =>
        PurchaseStore(db).CreateAsync(
            Fields(),
            [new PurchaseItemInput(NayaxSqsSaleFixture.ProductA, 10m, unitCost)],
            file: null,
            supplierOrderCompletion: null,
            attachmentReservation: null,
            CancellationToken.None);

    /// <summary>
    /// Corrects the purchase's one line to <paramref name="unitCost"/> at the same quantity, which
    /// rewrites its stock movement in place and adds none.
    /// </summary>
    private static Task<PurchaseRecord?> EditPurchaseAsync(
        AppDbContext db, int purchaseId, decimal unitCost) =>
        PurchaseStore(db).UpdateAsync(
            purchaseId,
            Fields(),
            [new PurchaseItemInput(NayaxSqsSaleFixture.ProductA, 10m, unitCost)],
            CancellationToken.None);

    private static async Task<(int? CostingQuantity, decimal? InventoryValue, decimal AverageUnitCost)>
        CostingPositionAsync(AppDbContext db)
    {
        var product = await db.Products.AsNoTracking()
            .SingleAsync(row => row.Id == NayaxSqsSaleFixture.ProductA);
        return (product.CostingQuantity, product.InventoryValue, product.AverageUnitCost);
    }

    /// <summary>
    /// The <strong>real</strong> replay - the real ledger read, the real weighted-average replay and
    /// the real staging - with <paramref name="interleave"/> run once after it has read the product's
    /// costing inputs and before it stages anything computed from them. That is the window the two
    /// purchase cases are about, and placing the interleaving inside the ledger read is what makes the
    /// order the one they are about rather than one the scheduler chose.
    ///
    /// <para>It is deliberately not <see cref="InterleavingReplay"/>: those cases ask whether a
    /// recognisable stale value reached the database, and these ask which of two <em>real</em> rebuilt
    /// positions stands, so the figures have to be the ones the production replay computes.</para>
    /// </summary>
    private static IRebuildProductCost InterleavedRealReplay(AppDbContext db, Func<Task> interleave) =>
        new RebuildProductCost(new InterleavingLedgerStore(new EfInventoryCostLedgerStore(db), interleave));

    private sealed class InterleavingLedgerStore(
        IInventoryCostLedgerStore inner, Func<Task> interleave) : IInventoryCostLedgerStore
    {
        private bool _interleaved;

        public async Task<InventoryCostLedger?> LoadAsync(
            long productId, bool forUpdate, CancellationToken cancellationToken)
        {
            var ledger = await inner.LoadAsync(productId, forUpdate, cancellationToken);

            if (!_interleaved)
            {
                _interleaved = true;
                await interleave();
            }

            return ledger;
        }

        public Task<InventoryCostLedger?> LoadAsOfAsync(
            long productId, DateTime asOf, CancellationToken cancellationToken) =>
            inner.LoadAsOfAsync(productId, asOf, cancellationToken);

        public void StageReplay(
            InventoryCostLedger ledger,
            IReadOnlyCollection<Inventory.Domain.Costing.CostReplayAdjustmentOutcome> adjustments,
            IReadOnlyCollection<Inventory.Domain.Costing.CostReplaySaleCost> recostedSales) =>
            inner.StageReplay(ledger, adjustments, recostedSales);

        public void StageProductPosition(InventoryCostLedger ledger, ProductCostPosition position) =>
            inner.StageProductPosition(ledger, position);
    }

    private static async Task<NayaxSqsProcessingOutcome> ProcessAsync(NayaxSqsSaleFixture fixture)
    {
        await using var db = fixture.Context();

        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(client => client.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new NayaxMachine { MachineID = NayaxSqsSaleFixture.MachineId }]);

        return await new ProcessNayaxSqsSale(
                NayaxSqsSaleFixture.Store(db),
                new FakeNayaxOperationCredentials(),
                nayax.Object,
                new NayaxSqsSlotProductResolver(nayax.Object),
                new EfNayaxSqsSenderActivityStore(db),
                new FakeClock(NayaxSqsSaleFixture.ReceivedAtUtc))
            .Handle("message-1", NayaxSqsRealMessage.Received, CancellationToken.None);
    }

    /// <summary>
    /// A replay with the interleaving the two cases above are about placed <em>inside</em> it: the
    /// other writer's work happens while this replay is running, which is exactly the window a drain
    /// cannot hold the write lock across.
    ///
    /// <para>What it then stages is a recognisable cost rather than a real rebuild, and that is the
    /// point of the double: a real replay stages the product's rebuilt costing position without
    /// saving it, so whether <see cref="StaleCost"/> is in the database afterwards says whether the
    /// drain saved or discarded a replay whose claim had gone. The value is one nothing legitimate
    /// produces.</para>
    /// </summary>
    private sealed class InterleavingReplay : IRebuildProductCost
    {
        /// <summary>The average unit cost this replay stages; nothing real ever produces it.</summary>
        public const decimal StaleCost = 999.99m;

        private readonly AppDbContext _db;
        private readonly Func<Task> _interleave;
        private bool _interleaved;

        public InterleavingReplay(AppDbContext db, Func<Task> interleave)
        {
            _db = db;
            _interleave = interleave;
        }

        public async Task<InventoryCostRebuildResult> RebuildAsync(
            long productId,
            DateTime? recostCompletedSalesFrom = null,
            bool dryRun = false,
            CancellationToken cancellationToken = default)
        {
            // The costing inputs are read first and the other writer's work happens after that read,
            // which is the window an acknowledgement has to survive: a replay that has already read
            // what it computes from cannot see a sale committed afterwards, whatever instant that
            // sale carries.
            var product = await _db.Products.SingleAsync(row => row.Id == productId, cancellationToken);

            if (!_interleaved)
            {
                _interleaved = true;
                await _interleave();
            }

            product.AverageUnitCost = StaleCost;

            return new InventoryCostRebuildResult
            {
                ProductId = productId,
                CostingQuantity = product.CostingQuantity ?? 0,
                InventoryValue = product.InventoryValue ?? 0m,
                AverageUnitCost = StaleCost,
            };
        }

        public Task<InventoryCostRebuildResult> RebuildCostingOnlyAsync(
            long productId,
            DateTime recostCompletedSalesFrom,
            CancellationToken cancellationToken = default) =>
            RebuildAsync(productId, recostCompletedSalesFrom, dryRun: false, cancellationToken);

        public Task<decimal?> GetAverageUnitCostAtAsync(
            long productId,
            DateTime saleTime,
            long? saleTransactionId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<decimal?>(null);
    }

    /// <summary>
    /// The sale-costing use case, refusing the first save the way the unique index on
    /// <c>(BusinessId, TransactionID)</c> would. It is a seam rather than a real race because the
    /// write lock makes the real race unreachable on one file - and the behaviour under test is what
    /// the store does when it is told the index refused, which is the same either way.
    /// </summary>
    private sealed class RefusingOnceCostSale(ICostSale inner) : ICostSale
    {
        public int Calls { get; private set; }

        public Task<SaleCostAssignment?> Handle(
            CostableSale sale, bool force = false, CancellationToken cancellationToken = default)
        {
            Calls++;

            return Calls == 1
                ? throw new DbUpdateException(
                    "An error occurred while saving the entity changes.",
                    new SqliteException(
                        "SQLite Error 19: 'UNIQUE constraint failed: NayaxSales.BusinessId, "
                            + "NayaxSales.TransactionID'.",
                        19))
                : inner.Handle(sale, force, cancellationToken);
        }
    }
}
