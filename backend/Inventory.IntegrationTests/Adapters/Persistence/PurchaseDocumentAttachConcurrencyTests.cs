using System.Data.Common;
using Inventory.Application.Purchases;
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
/// Two requests attaching a document to the same purchase at the same time (issue #976).
///
/// <para>This is the race "attach only if empty" exists for. It is a check-then-write rule, and a
/// check-then-write rule that is not serialised is not a rule: both requests read a purchase with no
/// document and both write their own stored name, so the second one's write silently replaces the
/// first one's - leaving a document nothing in the database names and nothing can ever reach again,
/// while its uploader was told the attach succeeded. Deleting a receipt is not recoverable by
/// rolling a transaction back, which is why the rule is that the loser is refused rather than that
/// the last writer wins. So the write takes <c>SqliteWriteLockTransaction</c>
/// (<c>BEGIN IMMEDIATE</c>) <em>before</em> its read and holds it through its commit, exactly as
/// issue #974's completion and issue #896's machine delete do.</para>
///
/// <para>Everything runs against a real SQLite <strong>file</strong> with one connection per writer,
/// because that is the only way to have two genuine writers: a shared in-memory database is one
/// connection, and InMemory has no locking at all. The barrier race is paired with a deterministic
/// interleaving driven from a <see cref="DbCommandInterceptor"/> - the same shape
/// <see cref="CompletedSupplierOrderCorrectionConcurrencyTests"/> uses, and for the same reason: a
/// barrier cannot be made to land one writer inside the other's check-then-write gap on demand, and
/// a regression test that only sometimes reaches the defect is not a regression test.</para>
///
/// <para>The sequential refusal - attaching to a purchase that already has a document - is in
/// <c>PurchaseWithoutDocumentApiTests</c>, over HTTP, where the <c>409</c> and the removal of the
/// loser's bytes are the contract.</para>
/// </summary>
public sealed class PurchaseDocumentAttachConcurrencyTests : IDisposable
{
    private const int BusinessA = 1;
    private const long ProductId = 100;

    private static readonly DateTime PurchaseDate = new(2026, 9, 5, 2, 0, 0, DateTimeKind.Utc);

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"inventory-purchase-document-attach-{Guid.NewGuid():N}.db");

    private readonly string _connectionString;
    private readonly string _impatientConnectionString;

    public PurchaseDocumentAttachConcurrencyTests()
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
            CreatedAtUtc = PurchaseDate,
        });
        db.Products.Add(new Product { Id = ProductId, BusinessId = BusinessA, Name = "Coke", Unit = "can" });
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

    /// <summary>
    /// A second attach landing inside the first one's read-then-write gap. It cannot: the first holds
    /// SQLite's write lock from before its read until its commit, so the second cannot even begin,
    /// and the purchase is left with exactly the first one's document. Without the lock the second
    /// one's write commits here and overwrites the stored name the first is about to report as
    /// attached.
    /// </summary>
    [Fact]
    public async Task A_second_attach_cannot_commit_inside_the_first_ones_read_and_write()
    {
        var purchaseId = await SeedUndocumentedPurchaseAsync();
        var competing = new AttachesWhileTheFirstOneReads(() => AttachAsync(ImpatientOptions(), purchaseId, "second"));

        await using (var db = TestAppDbContext.For(OptionsWith(competing), BusinessA))
        {
            var attachment = await new EfPurchaseStore(db, TestCostingUseCases.Rebuild(db), TestClocks.Fixed).AttachDocumentAsync(
                purchaseId, Document("first"), attachmentReservation: null, CancellationToken.None);

            Assert.Equal(PurchaseDocumentAttachOutcome.Attached, attachment.Outcome);
        }

        Assert.True(competing.Ran, "The competing attach was never attempted, so nothing was proved.");
        Assert.False(competing.Committed, "A second attach committed inside the first one's transaction.");
        Assert.IsType<SqliteException>(competing.Failure);

        var purchase = await ReadAsync(purchaseId);
        Assert.Equal(
            ("first.jpg", "first-stored.jpg", "image/jpeg", 3L),
            (purchase.FileName, purchase.StoredFileName, purchase.ContentType, purchase.FileSizeBytes));
    }

    /// <summary>
    /// The same pair as two genuine simultaneous requests. What this adds is the other failure mode:
    /// neither writer may answer with a provider lock error instead of a decision, whichever takes
    /// the write lock first - and exactly one of them attaches, with the purchase holding that
    /// winner's document and nobody else's.
    /// </summary>
    [Fact]
    public async Task Two_simultaneous_attaches_leave_exactly_one_document_and_one_refusal()
    {
        var purchaseId = await SeedUndocumentedPurchaseAsync();

        var (first, second) = await RaceAsync(
            () => AttachAsync(Options(), purchaseId, "first"),
            () => AttachAsync(Options(), purchaseId, "second"));

        AssertNoProviderFailure(first, second);
        var outcomes = new[] { first.Outcome, second.Outcome };
        Assert.Single(outcomes, PurchaseDocumentAttachOutcome.Attached);
        Assert.Single(outcomes, PurchaseDocumentAttachOutcome.AlreadyHasDocument);

        // The stored document is the winner's, and all four values are the winner's together.
        var winner = first.Outcome == PurchaseDocumentAttachOutcome.Attached ? "first" : "second";
        var purchase = await ReadAsync(purchaseId);
        Assert.Equal(
            ($"{winner}.jpg", $"{winner}-stored.jpg", "image/jpeg", 3L),
            (purchase.FileName, purchase.StoredFileName, purchase.ContentType, purchase.FileSizeBytes));
    }

    private async Task<int> SeedUndocumentedPurchaseAsync()
    {
        await using var db = TestAppDbContext.For(Options(), BusinessA);
        var purchase = await new EfPurchaseStore(db, TestCostingUseCases.Rebuild(db), TestClocks.Fixed).CreateAsync(
            new PurchaseFields("No receipt", null, null, null, null, PurchaseDate, null),
            [],
            file: null,
            supplierOrderCompletion: null,
            attachmentReservation: null,
            CancellationToken.None);
        return purchase.Id;
    }

    private static async Task<AttachAnswer> AttachAsync(
        DbContextOptions<AppDbContext> options, int purchaseId, string name)
    {
        await using var db = TestAppDbContext.For(options, BusinessA);
        try
        {
            var attachment = await new EfPurchaseStore(db, TestCostingUseCases.Rebuild(db), TestClocks.Fixed).AttachDocumentAsync(
                purchaseId, Document(name), attachmentReservation: null, CancellationToken.None);
            return new AttachAnswer(attachment.Outcome, null);
        }
        catch (Exception failure)
        {
            return new AttachAnswer(null, failure);
        }
    }

    private static PurchaseFileMetadata Document(string name) =>
        new($"{name}-stored.jpg", "image/jpeg", $"{name}.jpg", 3);

    private async Task<Purchase> ReadAsync(int purchaseId)
    {
        await using var verify = TestAppDbContext.Unrestricted(Options());
        return await verify.Receipts.AsNoTracking().SingleAsync(purchase => purchase.Id == purchaseId);
    }

    /// <summary>Releases both attaches at the same instant and returns what each of them answered.</summary>
    private static async Task<(AttachAnswer First, AttachAnswer Second)> RaceAsync(
        Func<Task<AttachAnswer>> first, Func<Task<AttachAnswer>> second)
    {
        using var barrier = new Barrier(2);

        var answers = new[] { first, second }
            .Select(operation => Task.Run(async () =>
            {
                barrier.SignalAndWait();
                return await operation();
            }))
            .ToArray();

        var resolved = await Task.WhenAll(answers);
        return (resolved[0], resolved[1]);
    }

    /// <summary>
    /// Neither writer may answer with a provider lock error. Both take the same write lock at the top
    /// of the operation, so the second waits for the first and then reads its committed document -
    /// which is what turns a race into a deliberate refusal rather than a <c>SQLITE_BUSY</c> the
    /// person uploading can do nothing with.
    /// </summary>
    private static void AssertNoProviderFailure(AttachAnswer first, AttachAnswer second)
    {
        Assert.Null(first.Failure);
        Assert.Null(second.Failure);
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

    /// <summary>What one attach answered: its outcome, or the exception it failed with.</summary>
    private sealed record AttachAnswer(PurchaseDocumentAttachOutcome? Outcome, Exception? Failure);

    /// <summary>
    /// Runs a competing attach at the first statement the first attach issues - its own authoritative
    /// read of the purchase. That is the instant an unserialised implementation was exposed at: with
    /// the write lock taken only at the first <em>write</em>, a second attach could read the same
    /// empty purchase and commit its own document before the first one wrote anything at all.
    /// </summary>
    private sealed class AttachesWhileTheFirstOneReads(Func<Task<AttachAnswer>> competing)
        : DbCommandInterceptor
    {
        private const string ReadTable = @"""Receipts""";

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
            ArgumentNullException.ThrowIfNull(command);
            Compete(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Compete(string? commandText)
        {
            if (_attempted || commandText?.Contains(ReadTable, StringComparison.Ordinal) != true) return;

            _attempted = true;
            Ran = true;
            try
            {
                // Blocking is deliberate: the competing request has to be resolved inside this
                // window, and it runs on its own connection, so nothing about the first attach's own
                // connection is waiting on it. `Task.Run` is what keeps blocking safe - it takes the
                // competing attach off the test runner's limited synchronization context, which this
                // thread is sitting on while it waits.
                var answer = Task.Run(competing).GetAwaiter().GetResult();
                if (answer.Failure is { } failure) Failure = failure;
                else Committed = answer.Outcome == PurchaseDocumentAttachOutcome.Attached;
            }
            catch (Exception failure)
            {
                Failure = failure;
            }
        }
    }
}
