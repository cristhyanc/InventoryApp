using Inventory.Application.Costing;
using Inventory.Application.Nayax;
using Inventory.Application.NayaxSqs;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Nayax;
using InventoryApi.Tests.Application.Time;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.NayaxSqs;

/// <summary>
/// Issue #1103's required rebuild regression, over relational SQLite and the real store, costing and
/// replay.
///
/// <para><strong>The defect this is about.</strong> A sale was saved and its inventory-cost replay ran
/// afterwards as a separate step, with what had to be replayed living in a dictionary for the length
/// of one call. If the replay failed, the sale stayed saved and the work was simply forgotten: the
/// next import found the transaction already stored, reported no affected products, and that product's
/// costing position stayed stale until its next sale. A queue reader that deleted a message on
/// "already stored" would have lost that replay for good - which is why the message may only be
/// deleted once its own products have no pending replay left.</para>
///
/// <para>The restart variant is the one that proves the record is durable rather than remembered:
/// the retry runs on a new context and a new store instance, built from the same database, holding
/// nothing from the attempt that failed.</para>
/// </summary>
public class NayaxSqsPendingRebuildTests
{
    private const string SenderId = "AIDAFICTIONALSENDER01";

    #region The required regression

    /// <summary>
    /// Persist succeeds, the replay fails: the sale is stored, the pending replay is written down with
    /// its attempt and its failure code, the product's costing position is untouched - a fatal replay
    /// stages nothing (issue #362) - and the message is <em>not</em> finished.
    /// </summary>
    [Fact]
    public async Task A_sale_whose_replay_fails_is_stored_and_the_work_is_remembered()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        await fixture.RecordSenderAsync(NayaxSqsSaleFixture.BusinessA, SenderId);

        var outcome = await ProcessAsync(fixture, replayFails: true);

        Assert.Equal(NayaxSqsDisposition.Transient, outcome.Disposition);
        Assert.Equal(NayaxSqsProcessingReason.RebuildPending, outcome.Reason);

        await using var db = fixture.Context();

        // The sale is durably stored, and stored as completed.
        var sale = await db.NayaxSales.AsNoTracking().SingleAsync();
        Assert.Equal(NayaxTransactionStatusIds.Completed, sale.TransactionStatusId);

        // The replay did not happen, and nothing it would have staged was saved.
        var product = await db.Products.AsNoTracking().SingleAsync(row => row.Id == NayaxSqsSaleFixture.ProductA);
        Assert.Equal(10, product.CostingQuantity);
        Assert.Equal(20m, product.InventoryValue);

        // And the work is written down, from the sale's own instant.
        var pending = await db.PendingInventoryCostRebuilds.AsNoTracking().SingleAsync();
        Assert.Equal(NayaxSqsSaleFixture.ProductA, pending.ProductId);
        Assert.Equal(NayaxSqsSaleFixture.SaleInstant, pending.FromInstantUtc);
        Assert.Equal(1, pending.AttemptCount);
        Assert.Equal(NayaxSqsSaleFixture.ReceivedAtUtc, pending.LastAttemptAtUtc);
        Assert.Equal("data_quality", pending.LastFailureCode);
    }

    /// <summary>
    /// The redelivery finds the existing sale, sees the product's pending replay, retries it - and
    /// only then is the message finished. Nothing is counted twice: one sale row, one unit consumed.
    /// </summary>
    [Fact]
    public async Task A_redelivered_message_retries_the_pending_replay_and_is_only_then_finished()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        await fixture.RecordSenderAsync(NayaxSqsSaleFixture.BusinessA, SenderId);

        await ProcessAsync(fixture, replayFails: true);

        var redelivered = await ProcessAsync(fixture, replayFails: false, messageId: "message-2");

        Assert.Equal(NayaxSqsDisposition.Done, redelivered.Disposition);
        Assert.Equal(NayaxSqsProcessingReason.AlreadyStored, redelivered.Reason);

        await using var db = fixture.Context();
        Assert.Single(await db.NayaxSales.AsNoTracking().ToListAsync());
        Assert.Empty(await db.PendingInventoryCostRebuilds.AsNoTracking().ToListAsync());

        var product = await db.Products.AsNoTracking().SingleAsync(row => row.Id == NayaxSqsSaleFixture.ProductA);
        Assert.Equal(9, product.CostingQuantity);
        Assert.Equal(18m, product.InventoryValue);
    }

    /// <summary>
    /// <strong>The restart variant.</strong> The retry runs on a new context and a new store - nothing
    /// is carried over from the attempt that failed - and it still replays the right product from the
    /// right instant, because the record is a row rather than a value in memory. The pending row is
    /// read back from the database first, which is what "it survived" means concretely.
    /// </summary>
    [Fact]
    public async Task The_pending_replay_survives_a_restart_between_the_failure_and_the_redelivery()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        await fixture.RecordSenderAsync(NayaxSqsSaleFixture.BusinessA, SenderId);

        await ProcessAsync(fixture, replayFails: true);

        // The process is gone: a new context, a new store, a new costing use case and a new replay,
        // holding nothing of the failed attempt.
        await using (var afterRestart = fixture.Context())
        {
            var pending = await afterRestart.PendingInventoryCostRebuilds.AsNoTracking().SingleAsync();
            Assert.Equal(NayaxSqsSaleFixture.ProductA, pending.ProductId);
            Assert.Equal(NayaxSqsSaleFixture.SaleInstant, pending.FromInstantUtc);

            var drained = await NayaxSqsSaleFixture.Store(afterRestart)
                .RebuildInventoryCostsAsync(CancellationToken.None);

            Assert.Empty(drained.StillPendingProductIds);
            Assert.Empty(drained.Failures);
        }

        await using var db = fixture.Context();
        Assert.Empty(await db.PendingInventoryCostRebuilds.AsNoTracking().ToListAsync());

        var product = await db.Products.AsNoTracking().SingleAsync(row => row.Id == NayaxSqsSaleFixture.ProductA);
        Assert.Equal(9, product.CostingQuantity);
    }

    #endregion

    #region The request-driven path records and drains the same rows

    /// <summary>
    /// The <c>lastSales</c> sync records pending replays too, and drains them the same way. The store
    /// is shared, so this fixes the existing failure mode for both paths: before, a replay that failed
    /// here was never retried either.
    /// </summary>
    [Fact]
    public async Task The_lastSales_path_records_a_pending_replay_and_a_later_drain_completes_it()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();

        await using (var failing = fixture.Context())
        {
            var store = NayaxSqsSaleFixture.Store(failing, AlwaysFailingReplay(failing));

            await store.PersistLatestSalesAsync(
                [NayaxSqsSaleFixture.LastSalesItem()], CancellationToken.None);

            var drained = await store.RebuildInventoryCostsAsync(CancellationToken.None);

            Assert.Equal([NayaxSqsSaleFixture.ProductA], drained.StillPendingProductIds);
            Assert.Single(drained.Failures);
        }

        await using (var after = fixture.Context())
        {
            var pending = await after.PendingInventoryCostRebuilds.AsNoTracking().SingleAsync();
            Assert.Equal(1, pending.AttemptCount);
            Assert.Equal(10, (await after.Products.AsNoTracking()
                .SingleAsync(row => row.Id == NayaxSqsSaleFixture.ProductA)).CostingQuantity);
        }

        // The next synchronization drains it, which is the retry that did not exist before.
        await using (var working = fixture.Context())
        {
            await NayaxSqsSaleFixture.Store(working).RebuildInventoryCostsAsync(CancellationToken.None);
        }

        await using var db = fixture.Context();
        Assert.Empty(await db.PendingInventoryCostRebuilds.AsNoTracking().ToListAsync());
        Assert.Equal(9, (await db.Products.AsNoTracking()
            .SingleAsync(row => row.Id == NayaxSqsSaleFixture.ProductA)).CostingQuantity);
    }

    #endregion

    #region What the row holds

    /// <summary>
    /// A second sale of the same product moves the instant <em>earlier</em> and never later: a replay
    /// from the earlier instant covers the later one, and the other way round would silently skip a
    /// sale's effect on cost. One row per product either way.
    /// </summary>
    [Fact]
    public async Task A_second_sale_only_ever_moves_the_pending_instant_earlier()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();

        var later = NayaxSqsSaleFixture.SaleInstant.AddHours(2);
        var earlier = NayaxSqsSaleFixture.SaleInstant.AddHours(-2);

        await using (var db = fixture.Context())
        {
            var store = NayaxSqsSaleFixture.Store(db, AlwaysFailingReplay(db));

            await store.PersistSqsSaleAsync(SaleAt(NayaxSqsSaleFixture.SaleInstant, 1), CancellationToken.None);
            await store.RebuildInventoryCostsAsync(CancellationToken.None);
        }

        await using (var db = fixture.Context())
        {
            var store = NayaxSqsSaleFixture.Store(db, AlwaysFailingReplay(db));

            // A later sale leaves the instant where it is...
            await store.PersistSqsSaleAsync(SaleAt(later, 2), CancellationToken.None);
            Assert.Equal(
                NayaxSqsSaleFixture.SaleInstant,
                (await db.PendingInventoryCostRebuilds.AsNoTracking().SingleAsync()).FromInstantUtc);

            // ... and an earlier one moves it back.
            await store.PersistSqsSaleAsync(SaleAt(earlier, 3), CancellationToken.None);
        }

        await using var read = fixture.Context();
        var pending = await read.PendingInventoryCostRebuilds.AsNoTracking().SingleAsync();
        Assert.Equal(earlier, pending.FromInstantUtc);
    }

    /// <summary>
    /// A declined sale records nothing to replay: it consumes no costing inventory, so there is
    /// nothing for a replay to do and nothing for the reader to wait on.
    /// </summary>
    [Fact]
    public async Task A_declined_sale_records_no_pending_replay()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();

        await using (var db = fixture.Context())
        {
            var persisted = await NayaxSqsSaleFixture.Store(db).PersistSqsSaleAsync(
                NayaxSqsSaleFixture.SqsSale(NayaxTransactionStatusIds.CancelledOrDeclined26),
                CancellationToken.None);

            Assert.Empty(persisted.AffectedProductIds);
        }

        await using var read = fixture.Context();
        Assert.Empty(await read.PendingInventoryCostRebuilds.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// A sale that resolved no local product records nothing either: an unresolved sale is costed from
    /// no product's inventory and consumed by no replay, so there is nothing to wait on and the
    /// message is finished.
    /// </summary>
    [Fact]
    public async Task A_sale_that_resolved_no_product_records_no_pending_replay()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();

        await using (var db = fixture.Context())
        {
            var persisted = await NayaxSqsSaleFixture.Store(db).PersistSqsSaleAsync(
                NayaxSqsSaleFixture.SqsSale(
                    NayaxTransactionStatusIds.Completed,

                    // A provider id this business has recorded on no product, and a name no
                    // product's own name matches - the existing unambiguous-name fallback would
                    // otherwise resolve it, which is what makes the second half of this necessary.
                    nayaxProductId: 999999,
                    productName: "A product nothing here sells"),
                CancellationToken.None);

            Assert.Empty(persisted.AffectedProductIds);
            Assert.Null((await db.NayaxSales.AsNoTracking().SingleAsync()).ProductId);
        }

        await using var read = fixture.Context();
        Assert.Empty(await read.PendingInventoryCostRebuilds.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// A pending row whose instant is at or before the product's transition-baseline cutoff has
    /// nothing to replay, so the row is removed rather than retried forever. That is the baseline
    /// doing its job, not a failure - and a row that stayed would hold every later message of that
    /// product's business behind it.
    /// </summary>
    [Fact]
    public async Task A_pending_row_at_or_before_the_baseline_cutoff_is_cleared_without_a_replay()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();

        await using (var db = fixture.Context())
        {
            db.PendingInventoryCostRebuilds.Add(new Inventory.Infrastructure.Models.PendingInventoryCostRebuild
            {
                ProductId = NayaxSqsSaleFixture.ProductA,
                FromInstantUtc = NayaxSqsSaleFixture.CutoffAt.AddDays(-1),
                CreatedAtUtc = NayaxSqsSaleFixture.ReceivedAtUtc,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.Context())
        {
            var drained = await NayaxSqsSaleFixture.Store(db)
                .RebuildInventoryCostsAsync(CancellationToken.None);

            Assert.Empty(drained.StillPendingProductIds);
            Assert.Empty(drained.Failures);
        }

        await using var read = fixture.Context();
        Assert.Empty(await read.PendingInventoryCostRebuilds.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// The drain reports rather than throws, and that distinction is what the reader needs: it has to
    /// tell "this message's own product is still failing" from "some other product of this business
    /// is". The request-driven synchronization is what turns a reported failure back into the
    /// data-quality exception an operator sees.
    /// </summary>
    [Fact]
    public async Task The_drain_reports_each_failing_product_rather_than_throwing()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();

        await using var db = fixture.Context();
        var store = NayaxSqsSaleFixture.Store(db, AlwaysFailingReplay(db));

        await store.PersistSqsSaleAsync(
            NayaxSqsSaleFixture.SqsSale(NayaxTransactionStatusIds.Completed), CancellationToken.None);

        var drained = await store.RebuildInventoryCostsAsync(CancellationToken.None);

        Assert.Equal([NayaxSqsSaleFixture.ProductA], drained.StillPendingProductIds);
        Assert.Contains(
            $"Product {NayaxSqsSaleFixture.ProductA}",
            Assert.Single(drained.Failures),
            StringComparison.Ordinal);
    }

    #endregion

    private static Inventory.Application.SalesSync.NayaxSqsSaleInput SaleAt(
        DateTime saleInstant, long transactionId)
    {
        var sale = NayaxSqsSaleFixture.SqsSale(
            NayaxTransactionStatusIds.Completed, transactionId: transactionId);

        return sale with
        {
            Report = new NayaxLastSalesReport
            {
                TransactionID = transactionId,
                MachineID = NayaxSqsSaleFixture.MachineId,
                SettlementValue = 3.5m,
                PaymentMethod = "Credit Card",
                ProductName = "Fictional Spring Water 600ml",
                NayaxProductId = NayaxSqsSaleFixture.NayaxProductId,
                TransactionStatusId = NayaxTransactionStatusIds.Completed,
                AuthorizationDateTimeGmt = new DateTimeOffset(saleInstant, TimeSpan.Zero),
            },
        };
    }

    /// <summary>
    /// The whole per-message path for one business: the real store, the real costing, the real replay
    /// (or one that refuses), the real sender stamp, and the gates a message has to pass.
    /// </summary>
    private static async Task<NayaxSqsProcessingOutcome> ProcessAsync(
        NayaxSqsSaleFixture fixture, bool replayFails, string messageId = "message-1")
    {
        await using var db = fixture.Context();

        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(client => client.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new NayaxMachine { MachineID = NayaxSqsSaleFixture.MachineId }]);

        var replay = replayFails ? AlwaysFailingReplay(db) : TestCostingUseCases.Rebuild(db);

        return await new ProcessNayaxSqsSale(
                NayaxSqsSaleFixture.Store(db, replay),
                new FakeNayaxOperationCredentials(),
                nayax.Object,
                new NayaxSqsSlotProductResolver(nayax.Object),
                new EfNayaxSqsSenderActivityStore(db),
                new FakeClock(NayaxSqsSaleFixture.ReceivedAtUtc))
            .Handle(messageId, NayaxSqsRealMessage.Received, CancellationToken.None);
    }

    /// <summary>
    /// A replay that always reports the product's history as fatally unreplayable, which is the one
    /// outcome the pending record exists for. It is a decorator over the real replay so everything
    /// else - the ledger read, the AVCO cost a sale is costed at - stays real.
    /// </summary>
    private static IRebuildProductCost AlwaysFailingReplay(AppDbContext db) =>
        new RefusingRebuild(TestCostingUseCases.Rebuild(db));

    private sealed class RefusingRebuild(IRebuildProductCost inner) : IRebuildProductCost
    {
        public Task<InventoryCostRebuildResult> RebuildAsync(
            long productId,
            DateTime? from = null,
            bool dryRun = false,
            CancellationToken cancellationToken = default) =>
            throw new InventoryCostDataQualityException(
                "The product has no opening cost before this sale.");

        public Task<InventoryCostRebuildResult> RebuildCostingOnlyAsync(
            long productId,
            DateTime recostCompletedSalesFrom,
            CancellationToken cancellationToken = default) =>
            throw new InventoryCostDataQualityException(
                "The product has no opening cost before this sale.");

        // The cost a sale is costed at is still the real one: what refuses here is the replay, not
        // the ledger read the sale's own cost comes from.
        public Task<decimal?> GetAverageUnitCostAtAsync(
            long productId,
            DateTime at,
            long? excludingTransactionId = null,
            CancellationToken cancellationToken = default) =>
            inner.GetAverageUnitCostAtAsync(productId, at, excludingTransactionId, cancellationToken);
    }
}
