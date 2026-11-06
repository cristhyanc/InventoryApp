using Inventory.Application.Costing;
using Inventory.Application.Nayax;
using Inventory.Application.NayaxSqs;
using Inventory.Domain.FinancialConfiguration;
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
