using System.Text;
using Inventory.Application.Costing;
using Inventory.Application.Imports;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Imports;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.NayaxSqs;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Application.Imports;

/// <summary>
/// Issue #1300's required concurrency case, over a real SQLite <strong>file</strong> with two
/// connections - which is the only way two writers and SQLite's own locking exist at all.
///
/// <para><strong>The defect these are about.</strong> The uploaded sales import read "does this
/// business already hold this transaction?" one row at a time, outside any transaction, and saved
/// everything at the end. Since issue #1103 it is not the only writer of a Nayax sale: the SQS queue
/// reader stores each transaction within seconds of the vend, through the same unique index on
/// <c>(BusinessId, TransactionID)</c>. A row the reader stored while the file was being read
/// therefore made the whole upload fail on that index with a <c>500</c> - after the operator had
/// waited minutes for it. The find-and-save now runs inside the same <c>BEGIN IMMEDIATE</c>
/// transaction both other sales paths take, with the stored rows read inside it, so the two
/// orderings below are the only ones there are and neither loses a sale.</para>
/// </summary>
public class NayaxSalesImportConcurrencyTests
{
    /// <summary>
    /// <strong>The required forced-interleaving case.</strong> The queue stores the transaction
    /// after the import has started - the file is read, the catalogue is read - and before the
    /// import takes the write lock. The import then finds that row inside its own transaction and
    /// treats it as an update: one sale row, no second insert, no <c>500</c>.
    /// </summary>
    [Fact]
    public async Task A_transaction_the_queue_stores_after_the_import_starts_is_updated_not_inserted()
    {
        await using var fixture = await NayaxSqsSaleFixture.OnDiskAsync();

        await using var importing = fixture.Context();
        await using var queueing = fixture.SecondWriter();

        // The interleaving, forced: the queue's save runs - and commits - between the import's read
        // of the file and the import's own write-locked read of what is stored.
        var queued = new QueueWriteBeforeTheLock(
            new EfNayaxSalesImportTransactions(importing),
            () => NayaxSqsSaleFixture.Store(queueing).PersistSqsSaleAsync(
                NayaxSqsSaleFixture.SqsSale(NayaxTransactionStatusIds.Completed),
                CancellationToken.None));

        var upload = await Import(importing, queued).Handle(File(NayaxTransactionStatusIds.Refunded));

        Assert.True(queued.QueueInserted, "the queue path should have inserted the sale first");
        Assert.Equal(0, upload.Imported);
        Assert.Equal(1, upload.Updated);

        await using var read = fixture.Context();
        var sale = Assert.Single(await read.NayaxSales.AsNoTracking().ToListAsync());
        Assert.Equal(NayaxSqsSaleFixture.TransactionId, sale.TransactionID);
        // The file is the source of truth for a status it carries, on a queue-stored sale as much as
        // any other (issue #1298), which is what makes "treated as an update" observable here.
        Assert.Equal(NayaxTransactionStatusIds.Refunded, sale.TransactionStatusId);
    }

    /// <summary>
    /// The other ordering, and the regression this change is: the queue's save is attempted
    /// <em>while</em> the import holds the write lock. It waits instead of committing into the
    /// middle of the import, so the import inserts the sale and the queue message then takes its own
    /// enrich path. One sale row, and neither side fails.
    ///
    /// <para>Without the write lock the queue's save committed during the import's loop and the
    /// import's own save was refused by the unique index - the <c>500</c> an operator used to get
    /// after waiting for the upload.</para>
    /// </summary>
    [Fact]
    public async Task A_queue_save_attempted_while_the_import_holds_the_lock_waits_for_it()
    {
        await using var fixture = await NayaxSqsSaleFixture.OnDiskAsync();

        await using var importing = fixture.Context();
        await using var queueing = fixture.SecondWriter();

        var rebuild = TestCostingUseCases.Rebuild(importing);
        var queued = new QueueWriteInsideTheLock(
            () => NayaxSqsSaleFixture.Store(queueing).PersistSqsSaleAsync(
                NayaxSqsSaleFixture.SqsSale(NayaxTransactionStatusIds.Completed),
                CancellationToken.None));

        var upload = await new ImportNayaxSales(
                new ClosedXmlNayaxSalesWorkbookReader(),
                new EfNayaxSalesImportStore(importing),
                new EfNayaxSalesImportTransactions(importing),
                queued.Around(TestCostingUseCases.CostSale(importing, rebuild)),
                rebuild,
                new EfNayaxProductLinkStore(importing))
            .Handle(File(NayaxTransactionStatusIds.Completed));

        // The import's own save went through: nothing committed into the middle of it.
        Assert.Equal(1, upload.Imported);
        Assert.Equal(0, upload.Updated);

        // And the queue message, once the lock was released, found the transaction already stored
        // and enriched it rather than inserting a second row.
        var queueResult = await queued.Completed;
        Assert.False(queueResult.Inserted);

        await using var read = fixture.Context();
        var sale = Assert.Single(await read.NayaxSales.AsNoTracking().ToListAsync());
        Assert.Equal(NayaxSqsSaleFixture.TransactionId, sale.TransactionID);
    }

    private static ImportNayaxSales Import(AppDbContext db, INayaxSalesImportTransactions transactions)
    {
        var rebuild = TestCostingUseCases.Rebuild(db);
        return new ImportNayaxSales(
            new ClosedXmlNayaxSalesWorkbookReader(),
            new EfNayaxSalesImportStore(db),
            transactions,
            TestCostingUseCases.CostSale(db, rebuild),
            rebuild,
            new EfNayaxProductLinkStore(db));
    }

    /// <summary>
    /// One uploaded row for the fixture's own transaction, machine and product, carrying
    /// <paramref name="statusId"/> so an update is visible as the status the file stated.
    /// </summary>
    private static NayaxSalesFileInput File(int statusId)
    {
        var csv =
            "TransactionID,TransactionStatusId,MachineID,NayaxProductId,SettlementValue,PaymentMethod,"
            + "ProductName,MachineName,AuthorizationDateTimeGMT\n"
            + $"{NayaxSqsSaleFixture.TransactionId},{statusId},{NayaxSqsSaleFixture.MachineId},"
            + $"{NayaxSqsSaleFixture.NayaxProductId},3.50,Credit Card,Fictional Spring Water 600ml,"
            + "Fictional Depot Machine 1,"
            + NayaxSqsSaleFixture.SaleInstant.ToString("yyyy-MM-ddTHH:mm:ssZ") + "\n";

        var content = Encoding.UTF8.GetBytes(csv);
        return new NayaxSalesFileInput("sales.csv", () => new MemoryStream(content));
    }

    /// <summary>
    /// Runs the queue's save, to completion, immediately before the import's own transaction is
    /// opened. That is the interleaving the issue asks for: the queue stored the transaction after
    /// the import started and before the import could have seen it.
    /// </summary>
    private sealed class QueueWriteBeforeTheLock : INayaxSalesImportTransactions
    {
        private readonly INayaxSalesImportTransactions _inner;
        private readonly Func<Task<Inventory.Application.SalesSync.NayaxSqsSalePersistResult>> _queueWrite;

        public QueueWriteBeforeTheLock(
            INayaxSalesImportTransactions inner,
            Func<Task<Inventory.Application.SalesSync.NayaxSqsSalePersistResult>> queueWrite)
        {
            _inner = inner;
            _queueWrite = queueWrite;
        }

        public bool QueueInserted { get; private set; }

        public async Task<INayaxSalesImportTransaction> BeginAsync(CancellationToken cancellationToken)
        {
            QueueInserted = (await _queueWrite()).Inserted;
            return await _inner.BeginAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Starts the queue's save from the middle of the import's write-locked loop - the point at which
    /// the import has read what is stored and has not yet saved - and gives it time to commit if it
    /// can. It cannot, because the import holds the write lock; a save that <em>could</em> commit
    /// there is the defect, and it shows up as the import's own save failing on the unique index.
    /// </summary>
    private sealed class QueueWriteInsideTheLock
    {
        private static readonly TimeSpan TimeToCommitIfItCould = TimeSpan.FromSeconds(1.5);

        private readonly Func<Task<Inventory.Application.SalesSync.NayaxSqsSalePersistResult>> _queueWrite;
        private Task<Inventory.Application.SalesSync.NayaxSqsSalePersistResult>? _started;

        public QueueWriteInsideTheLock(
            Func<Task<Inventory.Application.SalesSync.NayaxSqsSalePersistResult>> queueWrite) =>
            _queueWrite = queueWrite;

        /// <summary>The queue save's own outcome, awaited after the import has committed.</summary>
        public Task<Inventory.Application.SalesSync.NayaxSqsSalePersistResult> Completed =>
            _started ?? throw new InvalidOperationException("The queue save was never started.");

        public ICostSale Around(ICostSale inner) => new Hook(this, inner);

        private async Task StartAndWaitAsync()
        {
            if (_started is not null)
                return;

            _started = Task.Run(_queueWrite);
            await Task.WhenAny(_started, Task.Delay(TimeToCommitIfItCould));
        }

        private sealed class Hook : ICostSale
        {
            private readonly QueueWriteInsideTheLock _owner;
            private readonly ICostSale _inner;

            public Hook(QueueWriteInsideTheLock owner, ICostSale inner)
            {
                _owner = owner;
                _inner = inner;
            }

            public async Task<SaleCostAssignment?> Handle(
                CostableSale sale,
                bool force = false,
                SaleCostPrecedenceScope precedence = SaleCostPrecedenceScope.WithoutLatestPurchase,
                CancellationToken cancellationToken = default)
            {
                await _owner.StartAndWaitAsync();
                return await _inner.Handle(sale, force, precedence, cancellationToken);
            }
        }
    }
}
