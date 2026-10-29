using Inventory.Application.Attachments;
using Inventory.Application.Documents;
using Inventory.Application.Expenses;
using Inventory.Domain.Expenses;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Two uploads at once, against real SQLite with one connection each (issue #968).
///
/// <para>These are the two races the quota exists for, and neither can be shown with a shared
/// in-memory database - that is one connection - or with the EF InMemory provider, which has no
/// locking at all. So every writer here gets its own connection to a real database <strong>file</strong>,
/// exactly as <see cref="PurchaseDocumentAttachConcurrencyTests"/> does.</para>
///
/// <list type="number">
///   <item><strong>Two uploads cannot both pass a check only one of them fits.</strong> Reserving
///   reads the usage, compares it with the allowance and writes the reservation inside one
///   <c>BEGIN IMMEDIATE</c> transaction, so the second caller waits, reads the first one's committed
///   reservation and is refused.</item>
///   <item><strong>Two replacements of one attachment cannot both take credit for the old
///   file.</strong> Each reserves against the attachment version it read; the commit writes only
///   while the expense is still at that version. Exactly one wins, and the business ends up charged
///   for the winning file alone - the regression test issue #968 asks for by name.</item>
/// </list>
/// </summary>
public sealed class AttachmentStorageQuotaConcurrencyTests : IDisposable
{
    private const int BusinessA = 1;
    private const long Limit = 1_000;

    private static readonly DateTime Now = new(2026, 10, 29, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"inventory-attachment-quota-race-{Guid.NewGuid():N}.db");

    private readonly string _connectionString;
    private readonly FakeClock _clock = new(Now);

    public AttachmentStorageQuotaConcurrencyTests()
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString();

        using var db = TestAppDbContext.Unrestricted(Options());
        db.Database.Migrate();
        db.Businesses.Add(new Business
        {
            Id = BusinessA,
            Name = "Vending Co",
            Status = BusinessStatus.Active,
            CreatedAtUtc = Now,
        });
        db.SaveChanges();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Two uploads of 600 bytes into a 1,000-byte allowance. Exactly one is reserved; the other is
    /// refused rather than both being told there is room. Without the write lock around the read,
    /// the comparison and the insert, both would read an empty business and both would pass.
    /// </summary>
    [Fact]
    public async Task Two_simultaneous_uploads_cannot_both_be_reserved()
    {
        var (first, second) = await RaceAsync(
            () => ReserveAsync(uploadBytes: 600),
            () => ReserveAsync(uploadBytes: 600));

        Assert.Null(first.Failure);
        Assert.Null(second.Failure);

        var reserved = new[] { first.Reservation, second.Reservation }.Where(r => r is not null).ToList();
        Assert.Single(reserved);
        Assert.Equal(600, reserved[0]!.ChargeableBytes);

        await using var verify = TestAppDbContext.Unrestricted(Options());
        Assert.Single(await verify.AttachmentStorageReservations.ToListAsync());
    }

    /// <summary>
    /// <strong>The regression test of issue #968.</strong> Storage is full, and two replacements of
    /// the same attachment are each reserved against the same old version and the same old size -
    /// so each is charged nothing, and each would commit on its own. Exactly one does. The other is
    /// refused with a conflict, its row change is not made, and the final usage is the winning
    /// file's size alone rather than two files' worth of credit for one old file.
    /// </summary>
    [Fact]
    public async Task Two_simultaneous_replacements_leave_exactly_one_winner_and_the_winners_bytes()
    {
        var expenseId = await SeedExpenseAsync("original.pdf", 1_000);

        var firstReservation = await ReserveAsync(uploadBytes: 1_000, expenseId);
        var secondReservation = await ReserveAsync(uploadBytes: 1_000, expenseId);
        Assert.NotNull(firstReservation.Reservation);
        Assert.NotNull(secondReservation.Reservation);
        Assert.Equal(0, firstReservation.Reservation!.ChargeableBytes);
        Assert.Equal(
            firstReservation.Reservation.RecordVersion, secondReservation.Reservation!.RecordVersion);

        var (first, second) = await RaceAsync(
            () => ReplaceAsync(expenseId, "first.pdf", firstReservation.Reservation),
            () => ReplaceAsync(expenseId, "second.pdf", secondReservation.Reservation));

        var replaced = new[] { first, second }.Where(answer => answer.Replaced is not null).ToList();
        var refused = new[] { first, second }.Where(answer => answer.Replaced is null).ToList();
        Assert.Single(replaced);
        Assert.Single(refused);
        Assert.IsType<AttachmentReplacementConflictException>(refused[0].Failure);

        await using var verify = TestAppDbContext.Unrestricted(Options());
        var expense = await verify.OperatingExpenses.AsNoTracking().SingleAsync();
        Assert.Equal(replaced[0].StoredFileName, expense.AttachmentStoredFileName);
        Assert.Equal(1_000, expense.AttachmentFileSizeBytes);

        // One replacement's worth of storage, and the loser's reservation is still there for its
        // own use case to release - never committed, never charged twice.
        var usage = await ReadUsageAsync();
        Assert.Equal(1_000, usage.StoredBytes);

        await using var own = TestAppDbContext.For(Options(), BusinessA);
        var store = new EfAttachmentQuotaStore(own, _clock);
        Assert.True(await store.ReleaseAsync(refused[0].Reservation!, CancellationToken.None));
        Assert.Equal(1_000, (await ReadUsageAsync()).TotalBytes);
    }

    private async Task<ReserveAnswer> ReserveAsync(long uploadBytes, int? expenseId = null)
    {
        try
        {
            await using var db = TestAppDbContext.For(Options(), BusinessA);
            var reservation = await new EfAttachmentQuotaStore(db, _clock).ReserveAsync(
                new AttachmentUploadRequest(
                    expenseId is null ? DocumentCategory.PurchaseDocument : DocumentCategory.ExpenseAttachment,
                    expenseId,
                    uploadBytes,
                    Limit),
                CancellationToken.None);
            return new ReserveAnswer(reservation, null);
        }
        catch (Exception failure)
        {
            return new ReserveAnswer(null, failure);
        }
    }

    private async Task<ReplaceAnswer> ReplaceAsync(
        int expenseId, string storedFileName, AttachmentUploadReservation reservation)
    {
        try
        {
            await using var db = TestAppDbContext.For(Options(), BusinessA);
            var updated = await new EfOperatingExpenseStore(db, _clock).UpdateAsync(
                expenseId,
                Fields(),
                new OperatingExpenseAttachmentMetadata("invoice.pdf", storedFileName, "application/pdf", 1_000),
                reservation,
                CancellationToken.None);
            return new ReplaceAnswer(updated?.AttachmentStoredFileName, storedFileName, reservation, null);
        }
        catch (Exception failure)
        {
            return new ReplaceAnswer(null, storedFileName, reservation, failure);
        }
    }

    private async Task<AttachmentStorageUsage> ReadUsageAsync()
    {
        await using var db = TestAppDbContext.For(Options(), BusinessA);
        return await new EfAttachmentQuotaStore(db, _clock).ReadUsageAsync(CancellationToken.None);
    }

    private async Task<int> SeedExpenseAsync(string storedFileName, long sizeBytes)
    {
        await using var db = TestAppDbContext.Unrestricted(Options());
        var expense = new OperatingExpense
        {
            BusinessId = BusinessA,
            ExpenseDate = Now,
            Category = OperatingExpenseCategory.Insurance,
            Description = "Cover",
            AmountExGst = 10m,
            GstAmount = 1m,
            TotalAmount = 11m,
            AttachmentFileName = "invoice.pdf",
            AttachmentStoredFileName = storedFileName,
            AttachmentContentType = "application/pdf",
            AttachmentFileSizeBytes = sizeBytes,
        };
        db.OperatingExpenses.Add(expense);
        await db.SaveChangesAsync();
        return expense.Id;
    }

    /// <summary>Releases both operations at the same instant and returns what each answered.</summary>
    private static async Task<(T First, T Second)> RaceAsync<T>(Func<Task<T>> first, Func<Task<T>> second)
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

    private static OperatingExpenseFields Fields() => new(
        Now, ExpenseCategory.Insurance, "Cover", 10m, 1m, 11m, null, null, null, null, null, null);

    private DbContextOptions<AppDbContext> Options() =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connectionString).Options;

    /// <summary>What one reservation attempt answered: the reservation, or the failure.</summary>
    private sealed record ReserveAnswer(AttachmentUploadReservation? Reservation, Exception? Failure);

    /// <summary>
    /// What one replacement answered: the stored name the expense ends up with when it committed,
    /// the stored name it tried to write, its reservation, and the failure when it was refused.
    /// </summary>
    private sealed record ReplaceAnswer(
        string? Replaced,
        string StoredFileName,
        AttachmentUploadReservation? Reservation,
        Exception? Failure);
}
