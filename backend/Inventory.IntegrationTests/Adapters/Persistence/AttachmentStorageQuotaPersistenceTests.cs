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
/// The attachment storage accounting against real SQLite (issue #968): what a business is charged,
/// what a reservation costs, what happens when one expires, and that none of it crosses a business.
///
/// <para>It is a relational test on purpose. Usage is two <c>SUM</c>s and a filtered one over the
/// reservations, a reservation is a row written inside <c>BEGIN IMMEDIATE</c>, and the commit's
/// re-validation is a read inside somebody else's transaction - none of which an in-memory provider
/// proves anything about. The serialisation of two uploads and the version check that decides
/// between two replacements are in <c>AttachmentStorageQuotaConcurrencyTests</c>, which needs two
/// genuine connections.</para>
/// </summary>
public sealed class AttachmentStorageQuotaPersistenceTests : IDisposable
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;
    private const long Limit = 1_000;

    private static readonly DateTime Now = new(2026, 10, 29, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"inventory-attachment-quota-{Guid.NewGuid():N}.db");

    private readonly string _connectionString;
    private readonly FakeClock _clock = new(Now);

    public AttachmentStorageQuotaPersistenceTests()
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString();

        using var db = TestAppDbContext.Unrestricted(Options());
        db.Database.Migrate();
        db.Businesses.Add(Business(BusinessA, "Vending Co"));
        db.Businesses.Add(Business(BusinessB, "Other Co"));
        db.SaveChanges();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Usage_is_the_stored_bytes_of_this_businesss_own_attachments()
    {
        await SeedPurchaseAsync(BusinessA, "a-purchase.jpg", 300);
        await SeedExpenseAsync(BusinessA, "a-expense.pdf", 200);
        await SeedPurchaseAsync(BusinessB, "b-purchase.jpg", 900);

        var usage = await ReadUsageAsync(BusinessA);

        Assert.Equal(500, usage.StoredBytes);
        Assert.Equal(0, usage.ReservedBytes);
        Assert.Equal(500, usage.TotalBytes);

        // The other business is charged for its own and nothing else, which is the isolation half of
        // the same answer.
        Assert.Equal(900, (await ReadUsageAsync(BusinessB)).StoredBytes);
    }

    /// <summary>
    /// A purchase with no document, and an attachment whose size was never recorded, both
    /// contribute nothing: a quota never charges for bytes nobody measured, and the reconciliation
    /// command is what reports them.
    /// </summary>
    [Fact]
    public async Task An_unrecorded_size_and_a_record_with_no_document_are_charged_as_nothing()
    {
        await SeedPurchaseAsync(BusinessA, storedFileName: null, sizeBytes: null);
        await SeedExpenseAsync(BusinessA, "a-expense.pdf", sizeBytes: null);

        Assert.Equal(0, (await ReadUsageAsync(BusinessA)).TotalBytes);
    }

    [Fact]
    public async Task An_unexpired_reservation_counts_and_an_expired_one_does_not()
    {
        var reservation = await ReserveAsync(BusinessA, uploadBytes: 400);
        Assert.NotNull(reservation);
        Assert.Equal(400, (await ReadUsageAsync(BusinessA)).ReservedBytes);

        _clock.UtcNow = reservation!.ExpiresAtUtc;

        Assert.Equal(0, (await ReadUsageAsync(BusinessA)).ReservedBytes);
    }

    [Fact]
    public async Task An_upload_that_lands_exactly_on_the_limit_is_reserved_and_the_next_byte_is_not()
    {
        await SeedPurchaseAsync(BusinessA, "a-purchase.jpg", 900);

        Assert.NotNull(await ReserveAsync(BusinessA, uploadBytes: 100));

        await using var db = TestAppDbContext.For(Options(), BusinessA);
        var store = new EfAttachmentQuotaStore(db, _clock);
        Assert.Null(await store.ReserveAsync(
            new AttachmentUploadRequest(DocumentCategory.PurchaseDocument, null, 1, Limit),
            CancellationToken.None));
    }

    /// <summary>
    /// A refused reservation writes nothing at all: there is no row to expire, and the business is
    /// charged exactly what it was before it tried.
    /// </summary>
    [Fact]
    public async Task A_refused_reservation_leaves_no_row_behind()
    {
        await SeedPurchaseAsync(BusinessA, "a-purchase.jpg", 1_000);

        Assert.Null(await ReserveAsync(BusinessA, uploadBytes: 1));

        await using var verify = TestAppDbContext.Unrestricted(Options());
        Assert.Empty(await verify.AttachmentStorageReservations.ToListAsync());
    }

    /// <summary>
    /// The replacement rule, computed from the stored row rather than from anything a caller says:
    /// a bigger file costs the difference, and an equal or smaller one costs nothing - so it is
    /// reserved even with the business already at its limit.
    /// </summary>
    [Theory]
    [InlineData(500L, 100L)]
    [InlineData(400L, 0L)]
    [InlineData(100L, 0L)]
    public async Task A_replacement_is_charged_only_its_net_increase(long uploadBytes, long expectedCharge)
    {
        // 900 of the 1,000 is already stored, so only the net increase can possibly fit: the
        // larger file is charged its 100-byte difference and lands exactly on the allowance.
        var expenseId = await SeedExpenseAsync(BusinessA, "a-expense.pdf", 400);
        await SeedPurchaseAsync(BusinessA, "a-purchase.jpg", 500);

        var reservation = await ReserveAsync(
            BusinessA, uploadBytes, DocumentCategory.ExpenseAttachment, expenseId);

        Assert.NotNull(reservation);
        Assert.Equal(expectedCharge, reservation!.ChargeableBytes);
        Assert.Equal(0, reservation.RecordVersion);
    }

    /// <summary>
    /// After a downgrade a business can be over its allowance. Nothing is deleted, and a
    /// same-size-or-smaller replacement still goes through - it is a larger one, and only a larger
    /// one, that is refused.
    /// </summary>
    [Fact]
    public async Task A_business_over_its_allowance_can_still_replace_without_growing()
    {
        var expenseId = await SeedExpenseAsync(BusinessA, "a-expense.pdf", 5_000);

        Assert.NotNull(await ReserveAsync(
            BusinessA, uploadBytes: 5_000, DocumentCategory.ExpenseAttachment, expenseId));
        Assert.NotNull(await ReserveAsync(
            BusinessA, uploadBytes: 4_000, DocumentCategory.ExpenseAttachment, expenseId));
        Assert.Null(await ReserveAsync(
            BusinessA, uploadBytes: 5_001, DocumentCategory.ExpenseAttachment, expenseId));
    }

    /// <summary>
    /// Expiry is read rather than swept, and the housekeeping only tidies: reserving removes this
    /// business's expired rows, and the answer would be the same if it did not.
    /// </summary>
    [Fact]
    public async Task Reserving_clears_this_businesss_expired_reservations()
    {
        var first = await ReserveAsync(BusinessA, uploadBytes: 400);
        _clock.UtcNow = first!.ExpiresAtUtc;

        var second = await ReserveAsync(BusinessA, uploadBytes: 900);

        Assert.NotNull(second);
        await using var verify = TestAppDbContext.Unrestricted(Options());
        var remaining = Assert.Single(await verify.AttachmentStorageReservations.ToListAsync());
        Assert.Equal(second!.Id, remaining.Id);
    }

    /// <summary>
    /// A reservation belongs to the business that took it. Another business cannot release it, see
    /// it in its own usage, or be charged for it - the ordinary tenant filters, with nothing added.
    /// </summary>
    [Fact]
    public async Task A_reservation_is_invisible_and_unreleasable_from_another_business()
    {
        var reservation = await ReserveAsync(BusinessA, uploadBytes: 400);

        Assert.Equal(0, (await ReadUsageAsync(BusinessB)).ReservedBytes);

        await using (var other = TestAppDbContext.For(Options(), BusinessB))
        {
            Assert.False(await new EfAttachmentQuotaStore(other, _clock)
                .ReleaseAsync(reservation!, CancellationToken.None));
        }

        Assert.Equal(400, (await ReadUsageAsync(BusinessA)).ReservedBytes);

        await using var own = TestAppDbContext.For(Options(), BusinessA);
        Assert.True(await new EfAttachmentQuotaStore(own, _clock)
            .ReleaseAsync(reservation!, CancellationToken.None));
        Assert.Equal(0, (await ReadUsageAsync(BusinessA)).ReservedBytes);
    }

    /// <summary>
    /// The reservation becomes the attachment's own usage in the same transaction as the row: the
    /// reservation row is gone, and the business is charged the file's size once rather than twice.
    /// </summary>
    [Fact]
    public async Task Committing_an_attachment_consumes_its_reservation_with_the_row()
    {
        var reservation = await ReserveAsync(
            BusinessA, uploadBytes: 400, DocumentCategory.ExpenseAttachment);

        await using (var db = TestAppDbContext.For(Options(), BusinessA))
        {
            await new EfOperatingExpenseStore(db, _clock).AddAsync(
                Fields(), Attachment("a-expense.pdf", 400), reservation, CancellationToken.None);
        }

        var usage = await ReadUsageAsync(BusinessA);
        Assert.Equal(400, usage.StoredBytes);
        Assert.Equal(0, usage.ReservedBytes);

        await using var verify = TestAppDbContext.Unrestricted(Options());
        Assert.Empty(await verify.AttachmentStorageReservations.ToListAsync());
    }

    /// <summary>
    /// A reservation that expired while the bytes were being written is re-validated inside the
    /// write transaction. There is still room, so the row commits normally - the expiry is not a
    /// refusal of its own.
    /// </summary>
    [Fact]
    public async Task An_expired_reservation_still_commits_while_the_upload_fits()
    {
        var reservation = await ReserveAsync(
            BusinessA, uploadBytes: 400, DocumentCategory.ExpenseAttachment);
        _clock.UtcNow = reservation!.ExpiresAtUtc;

        await using (var db = TestAppDbContext.For(Options(), BusinessA))
        {
            var record = await new EfOperatingExpenseStore(db, _clock).AddAsync(
                Fields(), Attachment("a-expense.pdf", 400), reservation, CancellationToken.None);
            Assert.Equal("a-expense.pdf", record.AttachmentStoredFileName);
        }

        Assert.Equal(400, (await ReadUsageAsync(BusinessA)).StoredBytes);
    }

    /// <summary>
    /// And one that expired into a business with no room left is refused inside that transaction:
    /// the expense is not created at all, which is what lets the use case remove the file it wrote.
    /// </summary>
    [Fact]
    public async Task An_expired_reservation_that_no_longer_fits_refuses_the_write()
    {
        var reservation = await ReserveAsync(
            BusinessA, uploadBytes: 400, DocumentCategory.ExpenseAttachment);
        _clock.UtcNow = reservation!.ExpiresAtUtc;

        // Somebody else's upload took the room in the meantime.
        await SeedPurchaseAsync(BusinessA, "a-purchase.jpg", 800);

        await using (var db = TestAppDbContext.For(Options(), BusinessA))
        {
            await Assert.ThrowsAsync<AttachmentStorageQuotaExceededException>(() =>
                new EfOperatingExpenseStore(db, _clock).AddAsync(
                    Fields(), Attachment("a-expense.pdf", 400), reservation, CancellationToken.None));
        }

        await using var verify = TestAppDbContext.Unrestricted(Options());
        Assert.Empty(await verify.OperatingExpenses.ToListAsync());
        Assert.Equal(800, (await ReadUsageAsync(BusinessA)).StoredBytes);
    }

    /// <summary>
    /// The expired replacement's re-validation is against the attachment's <em>current</em> size,
    /// not the one the reservation was computed from: a business at its limit may still replace a
    /// file with one the same size, however long the upload took.
    /// </summary>
    [Fact]
    public async Task An_expired_replacement_is_revalidated_against_the_current_attachment_size()
    {
        var expenseId = await SeedExpenseAsync(BusinessA, "a-expense.pdf", 1_000);
        var reservation = await ReserveAsync(
            BusinessA, uploadBytes: 1_000, DocumentCategory.ExpenseAttachment, expenseId);
        Assert.Equal(0, reservation!.ChargeableBytes);
        _clock.UtcNow = reservation.ExpiresAtUtc;

        await using (var db = TestAppDbContext.For(Options(), BusinessA))
        {
            var updated = await new EfOperatingExpenseStore(db, _clock).UpdateAsync(
                expenseId, Fields(), Attachment("replacement.pdf", 1_000), reservation, CancellationToken.None);
            Assert.Equal("replacement.pdf", updated!.AttachmentStoredFileName);
        }

        Assert.Equal(1_000, (await ReadUsageAsync(BusinessA)).StoredBytes);
    }

    /// <summary>
    /// A replacement commits only while the attachment is still at the version its reservation was
    /// computed against. One that moved on is refused with a conflict and changes nothing - the
    /// sequential half of the race <c>AttachmentStorageQuotaConcurrencyTests</c> drives for real.
    /// </summary>
    [Fact]
    public async Task A_replacement_whose_attachment_moved_on_is_refused_as_a_conflict()
    {
        var expenseId = await SeedExpenseAsync(BusinessA, "a-expense.pdf", 100);
        var stale = await ReserveAsync(
            BusinessA, uploadBytes: 100, DocumentCategory.ExpenseAttachment, expenseId);

        // Another replacement commits first, moving the attachment version on.
        var winner = await ReserveAsync(
            BusinessA, uploadBytes: 100, DocumentCategory.ExpenseAttachment, expenseId);
        await using (var db = TestAppDbContext.For(Options(), BusinessA))
        {
            await new EfOperatingExpenseStore(db, _clock).UpdateAsync(
                expenseId, Fields(), Attachment("winner.pdf", 100), winner, CancellationToken.None);
        }

        await using (var db = TestAppDbContext.For(Options(), BusinessA))
        {
            await Assert.ThrowsAsync<AttachmentReplacementConflictException>(() =>
                new EfOperatingExpenseStore(db, _clock).UpdateAsync(
                    expenseId, Fields(), Attachment("loser.pdf", 100), stale, CancellationToken.None));
        }

        await using var verify = TestAppDbContext.Unrestricted(Options());
        var expense = await verify.OperatingExpenses.AsNoTracking().SingleAsync();
        Assert.Equal("winner.pdf", expense.AttachmentStoredFileName);
        Assert.Equal(1, expense.AttachmentVersion);
        Assert.Equal(100, (await ReadUsageAsync(BusinessA)).StoredBytes);
    }

    /// <summary>
    /// Deleting the record releases its bytes when that change commits, with nothing to decrement:
    /// usage is summed from the records that refer to a document, so a deleted one stops counting
    /// by itself - even if the physical file is still there for the reconciliation to report.
    /// </summary>
    [Fact]
    public async Task Deleting_a_record_releases_its_bytes()
    {
        var expenseId = await SeedExpenseAsync(BusinessA, "a-expense.pdf", 400);
        await SeedPurchaseAsync(BusinessA, "a-purchase.jpg", 300);
        Assert.Equal(700, (await ReadUsageAsync(BusinessA)).StoredBytes);

        await using (var db = TestAppDbContext.For(Options(), BusinessA))
        {
            await new EfOperatingExpenseStore(db, _clock).DeleteAsync(expenseId, CancellationToken.None);
        }

        Assert.Equal(300, (await ReadUsageAsync(BusinessA)).StoredBytes);
    }

    private async Task<AttachmentStorageUsage> ReadUsageAsync(int businessId)
    {
        await using var db = TestAppDbContext.For(Options(), businessId);
        return await new EfAttachmentQuotaStore(db, _clock).ReadUsageAsync(CancellationToken.None);
    }

    private async Task<AttachmentUploadReservation?> ReserveAsync(
        int businessId,
        long uploadBytes,
        DocumentCategory category = DocumentCategory.PurchaseDocument,
        int? recordId = null)
    {
        await using var db = TestAppDbContext.For(Options(), businessId);
        return await new EfAttachmentQuotaStore(db, _clock).ReserveAsync(
            new AttachmentUploadRequest(category, recordId, uploadBytes, Limit), CancellationToken.None);
    }

    private async Task SeedPurchaseAsync(int businessId, string? storedFileName, long? sizeBytes)
    {
        await using var db = TestAppDbContext.Unrestricted(Options());
        db.Receipts.Add(new Purchase
        {
            BusinessId = businessId,
            Title = "Restock",
            PurchaseDate = Now,
            FileName = storedFileName is null ? null : "receipt.jpg",
            StoredFileName = storedFileName,
            ContentType = storedFileName is null ? null : "image/jpeg",
            FileSizeBytes = storedFileName is null ? null : sizeBytes,
        });
        await db.SaveChangesAsync();
    }

    private async Task<int> SeedExpenseAsync(int businessId, string storedFileName, long? sizeBytes)
    {
        await using var db = TestAppDbContext.Unrestricted(Options());
        var expense = new OperatingExpense
        {
            BusinessId = businessId,
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

    private static OperatingExpenseFields Fields() => new(
        Now, ExpenseCategory.Insurance, "Cover", 10m, 1m, 11m, null, null, null, null, null, null);

    private static OperatingExpenseAttachmentMetadata Attachment(string storedFileName, long sizeBytes) =>
        new("invoice.pdf", storedFileName, "application/pdf", sizeBytes);

    private static Business Business(int id, string name) => new()
    {
        Id = id,
        Name = name,
        Status = BusinessStatus.Active,
        CreatedAtUtc = Now,
    };

    private DbContextOptions<AppDbContext> Options() =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connectionString).Options;
}
