using System.Data.Common;
using Inventory.Application.Purchases;
using Inventory.Application.SupplierOrders;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Correcting a linked purchase while another request completes the order it delivered (issue #974).
///
/// <para>This is the race the refusal exists for. "Does any receipt on this purchase belong to a
/// completed supplier order?" is a check-then-write rule, and a check-then-write rule that is not
/// serialised is not a rule: a purchase save carrying a completion decision can close the order in
/// the window between the check and the correction, and the correction then rewrites or removes the
/// very receipts that order was closed on - while the status rollup, which deliberately preserves
/// <c>ClosedPartiallyDelivered</c>, leaves it reading completed. The order would be recording a
/// delivery no purchase holds, or a different delivery from the one it was closed on, which is
/// exactly the state refusing the correction exists to prevent. So the correction takes the same
/// <c>SqliteWriteLockTransaction</c> (<c>BEGIN IMMEDIATE</c>) the completion path takes, <em>before</em>
/// the guard's read, and holds it through its commit.</para>
///
/// <para><strong>The window is opened deliberately rather than hoped for.</strong> A barrier race
/// releases two writers together, but it cannot land one of them inside the other's check-then-write
/// gap on demand - and a regression test that only sometimes reaches the defect is not a regression
/// test. So the two tests that prove the rule drive a competing completion from a
/// <see cref="DbCommandInterceptor"/> on the correction's own context, at the first statement
/// <em>after</em> the guard has answered: precisely the instant the old code held no write lock. The
/// competing save runs on its own connection, as a second request does, and that connection is
/// deliberately impatient (<c>Default Timeout=0</c>) so that "it cannot begin" is answered in
/// milliseconds instead of after SQLite's default thirty-second wait. In production that writer waits
/// and is then refused as stale, which <see cref="SupplierOrderPartialCompletionTests"/> covers; what
/// matters here is that it cannot commit <em>inside</em> the correction.</para>
///
/// <para>Everything runs against a real SQLite <strong>file</strong> with one connection per writer,
/// because that is the only way to have two genuine writers: a shared in-memory database is one
/// connection, and InMemory has no locking at all. The barrier race is kept beside the two
/// deterministic tests for what it does prove - that two real writers resolve to one coherent outcome
/// and that neither answers with a provider lock error instead of a decision. It is the same shape as
/// <see cref="LocalMachineDeletionConcurrencyTests"/> (issue #896), which pairs a barrier race with an
/// interceptor for the same reason.</para>
///
/// <para>The sequential refusals - an edit and a delete of a purchase whose order is <em>already</em>
/// completed - stay where they were, in
/// <c>SupplierOrderPartialCompletionTests.A_purchase_a_completed_order_was_closed_on_can_be_neither_edited_nor_deleted</c>.</para>
/// </summary>
public sealed class CompletedSupplierOrderCorrectionConcurrencyTests : IDisposable
{
    private const int BusinessA = 1;
    private const long ProductId = 100;
    private const int SupplierId = 1;
    private const decimal UnitCost = 2.50m;
    private const decimal Ordered = 24m;
    private const decimal FirstDelivery = 10m;
    private const decimal FinalDelivery = 2m;
    private const decimal CorrectedDelivery = 4m;

    private static readonly DateTime OrderDate = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FirstDeliveryDate = new(2026, 9, 5, 2, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FinalDeliveryDate = new(2026, 9, 12, 2, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime DecidedAt = new(2026, 9, 12, 3, 30, 0, DateTimeKind.Utc);

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"inventory-completed-order-correction-{Guid.NewGuid():N}.db");

    private readonly string _connectionString;
    private readonly string _impatientConnectionString;

    public CompletedSupplierOrderCorrectionConcurrencyTests()
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString();
        _impatientConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            // One second, not the ADO.NET "0 means wait forever" and not SQLite's default thirty.
            DefaultTimeout = 1,
        }.ToString();

        using var db = TestAppDbContext.Unrestricted(Options());
        db.Database.Migrate();
        db.Businesses.Add(new Business
        {
            Id = BusinessA,
            Name = "Vending Co",
            Status = BusinessStatus.Active,
            CreatedAtUtc = OrderDate,
        });
        db.Products.Add(new Product { Id = ProductId, BusinessId = BusinessA, Name = "Coke", Unit = "can" });
        db.Suppliers.Add(new Supplier { Id = SupplierId, BusinessId = BusinessA, Name = "Costco" });
        // An opening position before the first purchase, so the weighted-average replay has somewhere
        // to start from, exactly as the other purchase-store tests arrange one.
        db.InventoryCostTransitionBaselines.Add(new InventoryCostTransitionBaseline
        {
            BusinessId = BusinessA,
            ProductId = ProductId,
            CutoffAt = OrderDate.AddDays(-1),
            HomeStockQuantity = 0,
            CostSource = InventoryCostBaselineSource.ManualAuthoritative,
        });
        db.SaveChanges();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        GC.SuppressFinalize(this);
    }

    // ------------------------------------------- the window, opened on purpose

    /// <summary>
    /// A completion committing in the correction's own check-then-write gap. It cannot: the edit holds
    /// SQLite's write lock from before the guard read until its commit, so the competing save cannot
    /// even begin, and the order is left open with the edit's reconciled receipt. Without the lock the
    /// completion commits here and the edit goes on to rewrite the receipt that order was closed on
    /// from 10 to 4 - a completed order recording a delivery it was not closed on.
    /// </summary>
    [Fact]
    public async Task A_completion_cannot_commit_inside_an_edit_of_the_purchase_it_would_close_on()
    {
        var (orderId, purchaseId) = await SeedDeliveredOrderAsync();
        var fingerprint = await PreviewFingerprintAsync(orderId);
        var competing = new CompletesTheOrderAfterTheGuard(() => CompeteToCompleteAsync(orderId, fingerprint));

        await using (var db = TestAppDbContext.For(OptionsWith(competing), BusinessA))
        {
            Assert.NotNull(await new EfPurchaseStore(db, TestCostingUseCases.Rebuild(db)).UpdateAsync(
                purchaseId,
                new PurchaseFields("Weekly restock", null, null, null, null, FirstDeliveryDate, SupplierId),
                [new PurchaseItemInput(ProductId, CorrectedDelivery, UnitCost)],
                CancellationToken.None));
        }

        AssertTheCompetingCompletionNeverBegan(competing);

        var state = await ReadAsync(orderId);
        Assert.Equal(SupplierOrderStatus.PartiallyReceived, state.Order.Status);
        Assert.Null(state.Order.CompletedWithUndeliveredAt);
        Assert.Null(state.Order.CompletedByDirectoryTenantId);
        Assert.Null(state.Order.CompletedByObjectId);
        Assert.Equal(CorrectedDelivery, QuantityOn(state, purchaseId));
        Assert.Equal(CorrectedDelivery, AllocatedFrom(state, purchaseId));
        Assert.Equal(CorrectedDelivery, Received(state));
        // The refused completion persisted no purchase of its own.
        Assert.Equal([purchaseId], state.PurchaseIds);
    }

    /// <summary>
    /// The same window, with the correction being a deletion - the worse half, because a delete that
    /// slipped through would leave a completed order recording a delivery whose purchase no longer
    /// exists at all. The delete holds the write lock across its guard and its commit, so the
    /// completion cannot begin, and the order is back to <c>Ordered</c> with nothing received and
    /// nothing completed.
    /// </summary>
    [Fact]
    public async Task A_completion_cannot_commit_inside_a_delete_of_the_purchase_it_would_close_on()
    {
        var (orderId, purchaseId) = await SeedDeliveredOrderAsync();
        var fingerprint = await PreviewFingerprintAsync(orderId);
        var competing = new CompletesTheOrderAfterTheGuard(() => CompeteToCompleteAsync(orderId, fingerprint));

        await using (var db = TestAppDbContext.For(OptionsWith(competing), BusinessA))
        {
            Assert.NotNull(await new EfPurchaseStore(db, TestCostingUseCases.Rebuild(db)).DeleteAsync(
                purchaseId, CancellationToken.None));
        }

        AssertTheCompetingCompletionNeverBegan(competing);

        var state = await ReadAsync(orderId);
        Assert.Equal(SupplierOrderStatus.Ordered, state.Order.Status);
        Assert.Null(state.Order.CompletedWithUndeliveredAt);
        Assert.Null(state.Order.CompletedByDirectoryTenantId);
        Assert.Null(state.Order.CompletedByObjectId);
        Assert.Empty(state.PurchaseIds);
        Assert.Empty(state.Allocations);
        Assert.Equal(0m, Received(state));
    }

    // ------------------------------------------- two real writers, released together

    /// <summary>
    /// The same pair as two genuine simultaneous requests. What this adds to the two tests above is
    /// the other failure mode: neither writer may answer with a provider lock error instead of a
    /// decision, whichever of them takes the write lock first, and whichever wins the state left
    /// behind has to be one of exactly two coherent ones.
    /// </summary>
    [Fact]
    public async Task A_completion_and_an_edit_of_the_purchase_it_closes_on_leave_exactly_one_outcome()
    {
        var (orderId, purchaseId) = await SeedDeliveredOrderAsync();
        var fingerprint = await PreviewFingerprintAsync(orderId);

        var (correctionFailure, completionFailure) = await RaceAsync(
            () => EditAsync(purchaseId, CorrectedDelivery),
            () => CompleteAsync(orderId, fingerprint));

        AssertNoProviderFailure(correctionFailure, completionFailure);
        var state = await ReadAsync(orderId);

        if (state.Order.Status == SupplierOrderStatus.ClosedPartiallyDelivered)
        {
            // The completion won. The edit is refused by the rule, naming the order, and the receipt
            // the order was closed on is exactly the one it was closed on.
            Assert.Null(completionFailure);
            AssertRefusedCorrection(correctionFailure, orderId);
            Assert.Equal(DecidedAt, state.Order.CompletedWithUndeliveredAt);
            Assert.Equal(FirstDelivery, QuantityOn(state, purchaseId));
            Assert.Equal(FirstDelivery, AllocatedFrom(state, purchaseId));
            // The completing purchase's own two units are a receipt like any other.
            Assert.Equal(FirstDelivery + FinalDelivery, Received(state));
        }
        else
        {
            // The edit won. The order is still open, nothing recorded a decision on it, and the
            // completion is refused because the balance it was shown is gone.
            Assert.Null(correctionFailure);
            AssertStaleCompletion(completionFailure);
            Assert.Equal(SupplierOrderStatus.PartiallyReceived, state.Order.Status);
            Assert.Null(state.Order.CompletedWithUndeliveredAt);
            Assert.Null(state.Order.CompletedByObjectId);
            Assert.Equal(CorrectedDelivery, QuantityOn(state, purchaseId));
            Assert.Equal(CorrectedDelivery, AllocatedFrom(state, purchaseId));
            Assert.Equal(CorrectedDelivery, Received(state));
            Assert.Equal([purchaseId], state.PurchaseIds);
        }
    }

    /// <summary>The deletion half of the barrier race, asserting the same two coherent outcomes.</summary>
    [Fact]
    public async Task A_completion_and_a_delete_of_the_purchase_it_closes_on_leave_exactly_one_outcome()
    {
        var (orderId, purchaseId) = await SeedDeliveredOrderAsync();
        var fingerprint = await PreviewFingerprintAsync(orderId);

        var (correctionFailure, completionFailure) = await RaceAsync(
            () => DeleteAsync(purchaseId),
            () => CompleteAsync(orderId, fingerprint));

        AssertNoProviderFailure(correctionFailure, completionFailure);
        var state = await ReadAsync(orderId);

        if (state.Order.Status == SupplierOrderStatus.ClosedPartiallyDelivered)
        {
            Assert.Null(completionFailure);
            AssertRefusedCorrection(correctionFailure, orderId);
            Assert.Equal(DecidedAt, state.Order.CompletedWithUndeliveredAt);
            // The receipt the order was closed on is still held by the purchase that recorded it.
            Assert.Contains(purchaseId, state.PurchaseIds);
            Assert.Equal(FirstDelivery, QuantityOn(state, purchaseId));
            Assert.Equal(FirstDelivery, AllocatedFrom(state, purchaseId));
            Assert.Equal(FirstDelivery + FinalDelivery, Received(state));
        }
        else
        {
            Assert.Null(correctionFailure);
            AssertStaleCompletion(completionFailure);
            Assert.Equal(SupplierOrderStatus.Ordered, state.Order.Status);
            Assert.Null(state.Order.CompletedWithUndeliveredAt);
            Assert.Null(state.Order.CompletedByObjectId);
            Assert.Empty(state.PurchaseIds);
            Assert.Empty(state.Allocations);
            Assert.Equal(0m, Received(state));
        }
    }

    // ------------------------------------------- the two operations

    /// <summary>The purchase correction, as a request carrying a reduced quantity performs it.</summary>
    private async Task EditAsync(int purchaseId, decimal quantity)
    {
        await using var db = TestAppDbContext.For(Options(), BusinessA);
        await new EfPurchaseStore(db, TestCostingUseCases.Rebuild(db)).UpdateAsync(
            purchaseId,
            new PurchaseFields("Weekly restock", null, null, null, null, FirstDeliveryDate, SupplierId),
            [new PurchaseItemInput(ProductId, quantity, UnitCost)],
            CancellationToken.None);
    }

    private async Task DeleteAsync(int purchaseId)
    {
        await using var db = TestAppDbContext.For(Options(), BusinessA);
        await new EfPurchaseStore(db, TestCostingUseCases.Rebuild(db)).DeleteAsync(
            purchaseId, CancellationToken.None);
    }

    /// <summary>The final purchase carrying the completion decision, on its own connection.</summary>
    private Task CompleteAsync(int orderId, string fingerprint) =>
        SavePurchaseAsync(Options(), FinalDeliveryDate, FinalDelivery, orderId, fingerprint);

    /// <summary>The same save from the impatient connection the interceptor drives it on.</summary>
    private Task CompeteToCompleteAsync(int orderId, string fingerprint) =>
        SavePurchaseAsync(ImpatientOptions(), FinalDeliveryDate, FinalDelivery, orderId, fingerprint);

    // ------------------------------------------- arrangement

    /// <summary>
    /// An order for 24 with 10 of them delivered on a real purchase, which is the purchase both
    /// corrections act on. The receipt is a real allocation rather than a hand-set
    /// <c>QuantityReceived</c>, because the authoritative received total <em>is</em> the sum of the
    /// allocations and the next write would discard anything else.
    /// </summary>
    private async Task<(int OrderId, int PurchaseId)> SeedDeliveredOrderAsync()
    {
        int orderId;
        await using (var seed = TestAppDbContext.For(Options(), BusinessA))
        {
            var order = new SupplierOrder
            {
                SupplierId = SupplierId,
                OrderDate = OrderDate,
                Status = SupplierOrderStatus.Ordered,
                Lines =
                {
                    new SupplierOrderLine
                    {
                        ProductId = ProductId, QuantityOrdered = Ordered, UnitPrice = UnitCost,
                    },
                },
            };
            seed.SupplierOrders.Add(order);
            await seed.SaveChangesAsync();
            orderId = order.Id;
        }

        var purchaseId = await SavePurchaseAsync(Options(), FirstDeliveryDate, FirstDelivery, null, null);
        return (orderId, purchaseId);
    }

    /// <summary>The fingerprint the operator's screen was shown, over the final purchase's two units.</summary>
    private async Task<string> PreviewFingerprintAsync(int orderId)
    {
        await using var db = TestAppDbContext.For(Options(), BusinessA);
        var preview = await new PreviewSupplierOrderCompletion(new EfSupplierOrderCompletionStore(db)).Handle(
            orderId,
            SupplierId,
            FinalDeliveryDate,
            [new PurchaseLineToReceive(ProductId, FinalDelivery)],
            CancellationToken.None);
        Assert.NotNull(preview);
        Assert.Equal(
            Ordered - FirstDelivery - FinalDelivery,
            Assert.Single(preview!.OutstandingLines).OutstandingQuantity);
        return preview.DecisionFingerprint;
    }

    private async Task<int> SavePurchaseAsync(
        DbContextOptions<AppDbContext> options,
        DateTime purchaseDate,
        decimal quantity,
        int? orderId,
        string? fingerprint)
    {
        await using var db = TestAppDbContext.For(options, BusinessA);
        var purchase = await new EfPurchaseStore(db, TestCostingUseCases.Rebuild(db)).CreateAsync(
            new PurchaseFields("Weekly restock", null, null, null, null, purchaseDate, SupplierId),
            [new PurchaseItemInput(ProductId, quantity, UnitCost)],
            new PurchaseFileMetadata("stored.jpg", "image/jpeg", "receipt.jpg", 3),
            orderId is { } id && fingerprint is not null
                ? new SupplierOrderCompletionDecision(
                    id,
                    fingerprint,
                    TestPurchaseUseCases.Operator.DirectoryTenantId,
                    TestPurchaseUseCases.Operator.ObjectId,
                    DecidedAt)
                : null,
            CancellationToken.None);
        return purchase.Id;
    }

    private DbContextOptions<AppDbContext> Options() =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connectionString).Options;

    private DbContextOptions<AppDbContext> ImpatientOptions() =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_impatientConnectionString).Options;

    private DbContextOptions<AppDbContext> OptionsWith(IInterceptor interceptor) =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connectionString)
            .AddInterceptors(interceptor)
            .Options;

    // ------------------------------------------- the race, and what it left

    /// <summary>
    /// Releases both operations at the same instant and returns what each one failed with, or
    /// <see langword="null"/> where it succeeded.
    /// </summary>
    private static async Task<(Exception? Correction, Exception? Completion)> RaceAsync(
        Func<Task> correction, Func<Task> completion)
    {
        using var barrier = new Barrier(2);

        var answers = new[] { correction, completion }
            .Select(operation => Task.Run(async () =>
            {
                barrier.SignalAndWait();
                try
                {
                    await operation();
                    return (Exception?)null;
                }
                catch (Exception failure)
                {
                    return failure;
                }
            }))
            .ToArray();

        var failures = await Task.WhenAll(answers);
        return (failures[0], failures[1]);
    }

    private sealed record RaceOutcome(
        SupplierOrder Order,
        IReadOnlyList<int> PurchaseIds,
        IReadOnlyList<PurchaseItem> Items,
        IReadOnlyList<SupplierOrderReceiptAllocation> Allocations);

    private async Task<RaceOutcome> ReadAsync(int orderId)
    {
        await using var verify = TestAppDbContext.Unrestricted(Options());
        return new RaceOutcome(
            await verify.SupplierOrders.AsNoTracking()
                .Include(order => order.Lines)
                .SingleAsync(order => order.Id == orderId),
            await verify.Receipts.AsNoTracking().OrderBy(receipt => receipt.Id)
                .Select(receipt => receipt.Id).ToListAsync(),
            await verify.ReceiptItems.AsNoTracking().OrderBy(item => item.Id).ToListAsync(),
            await verify.SupplierOrderReceiptAllocations.AsNoTracking()
                .OrderBy(allocation => allocation.Id).ToListAsync());
    }

    private static decimal Received(RaceOutcome state) =>
        state.Order.Lines.Sum(line => line.QuantityReceived);

    private static decimal QuantityOn(RaceOutcome state, int purchaseId) =>
        state.Items.Where(item => item.ReceiptId == purchaseId).Sum(item => item.Quantity);

    private static decimal AllocatedFrom(RaceOutcome state, int purchaseId)
    {
        var itemIds = state.Items.Where(item => item.ReceiptId == purchaseId).Select(item => item.Id).ToList();
        return state.Allocations
            .Where(allocation => itemIds.Contains(allocation.ReceiptItemId))
            .Sum(allocation => allocation.QuantityApplied);
    }

    /// <summary>
    /// The competing completion got no further than trying to open its transaction: the correction's
    /// write lock was already held, so SQLite refused to let a second writer begin. This is the
    /// assertion the whole repair turns on - with the guard outside the write lock the save commits
    /// here instead.
    /// </summary>
    private static void AssertTheCompetingCompletionNeverBegan(CompletesTheOrderAfterTheGuard competing)
    {
        Assert.True(competing.Ran, "The competing completion was never attempted, so nothing was proved.");
        Assert.False(competing.Committed, "A completion committed inside the correction's transaction.");
        Assert.IsType<SqliteException>(competing.Failure);
    }

    /// <summary>
    /// Neither writer may answer with a provider lock error. Both take the same write lock at the
    /// top of their operation, so the second one waits for the first and then reads its committed
    /// state - which is what turns a race into a deliberate refusal rather than a <c>SQLITE_BUSY</c>
    /// an operator can do nothing with.
    /// </summary>
    private static void AssertNoProviderFailure(Exception? correction, Exception? completion)
    {
        Assert.True(
            correction is null or InvalidOperationException,
            $"The correction failed with {correction?.GetType().Name}: {correction?.Message}");
        Assert.True(
            completion is null or InvalidOperationException,
            $"The completion failed with {completion?.GetType().Name}: {completion?.Message}");
        Assert.True(
            correction is null || completion is null,
            "Both writers were refused, so neither outcome happened at all.");
    }

    private static void AssertRefusedCorrection(Exception? correction, int orderId) =>
        Assert.Contains(
            $"supplier order #{orderId}",
            Assert.IsType<InvalidOperationException>(correction).Message,
            StringComparison.Ordinal);

    private static void AssertStaleCompletion(Exception? completion) =>
        Assert.Contains(
            "changed after the completion choice was shown",
            Assert.IsType<InvalidOperationException>(completion).Message,
            StringComparison.Ordinal);

    /// <summary>
    /// Runs a competing completion at the first statement the correction issues <em>after</em> its
    /// completed-order guard has read, which is the one interleaving the guard cannot see by itself.
    /// The guard's query is recognised by the table it reads, and it is the first statement in both
    /// corrections to touch that table; the completion is then driven at the very next statement,
    /// because that - and not the guard's own read, which holds a read lock while it runs - is the
    /// instant the old code was exposed at.
    /// </summary>
    private sealed class CompletesTheOrderAfterTheGuard(Func<Task> completion)
        : DbCommandInterceptor, IDbTransactionInterceptor
    {
        private const string GuardedTable = "SupplierOrderReceiptAllocations";

        private bool _guardHasRead;
        private bool _attempted;

        /// <summary>Whether the window was reached at all; a test that never reaches it proves nothing.</summary>
        public bool Ran { get; private set; }

        public bool Committed { get; private set; }

        public Exception? Failure { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Compete(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Compete(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        /// <summary>
        /// The other shape the window can take, and the one the edit actually had: nothing at all
        /// happened between the guard's read and the correction opening its transaction, so the
        /// instant to compete at is that opening. A transaction EF starts for the caller is
        /// intercepted here; the write lock the repair takes is begun on the connection directly and
        /// handed to EF, so it deliberately does not come through this - which is the point, since
        /// by then the lock is already held.
        /// </summary>
        public ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection,
            TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            Compete(commandText: null);
            return ValueTask.FromResult(result);
        }

        private void Compete(DbCommand command)
        {
            ArgumentNullException.ThrowIfNull(command);
            Compete(command.CommandText);
        }

        private void Compete(string? commandText)
        {
            if (_attempted) return;

            if (!_guardHasRead)
            {
                _guardHasRead = commandText?.Contains(GuardedTable, StringComparison.Ordinal) == true;
                return;
            }

            _attempted = true;
            Ran = true;
            try
            {
                // Blocking is deliberate: the competing request has to be resolved inside this
                // window, and it runs on its own connection, so nothing about the correction's own
                // connection is waiting on it. `Task.Run` is what keeps blocking safe - it takes the
                // competing save off the test runner's limited synchronization context, which this
                // thread is sitting on while it waits.
                Task.Run(completion).GetAwaiter().GetResult();
                Committed = true;
            }
            catch (Exception failure)
            {
                Failure = failure;
            }
        }
    }
}
