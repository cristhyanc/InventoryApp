using Inventory.Application.Documents;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Documents.Reconciliation;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Documents;

/// <summary>
/// The human-run attachment storage reconciliation (issue #968): what it reports, what it fills in,
/// and - the part that matters most - what it never touches.
///
/// <para>A document's bytes live outside the database, so the two can disagree in exactly three
/// ways, and the quota resolves all three in the business's favour: a file nothing refers to is
/// charged to nobody, a record whose file is gone charges for storage that is not there, and a
/// record whose size was never recorded charges nothing at all. None of those is visible from
/// inside the application, so this is the operator's read of the truth - and it is the one place
/// that can say so per business.</para>
///
/// <para>It runs against real SQLite with the real migration history, because the reads are across
/// businesses on an unrestricted context and the write is a size recorded on a tenant-owned row.</para>
/// </summary>
public sealed class AttachmentStorageReconcilerTests : IDisposable
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;

    private static readonly DateTime Now = new(2026, 10, 29, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"inventory-attachment-reconcile-{Guid.NewGuid():N}.db");

    private readonly string _connectionString;

    public AttachmentStorageReconcilerTests()
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

    /// <summary>
    /// A clean business: every record's file is there and every size is recorded, so there is
    /// nothing to report and the run exits zero.
    /// </summary>
    [Fact]
    public async Task A_business_whose_storage_matches_its_records_needs_no_attention()
    {
        var purchaseId = await SeedPurchaseAsync(BusinessA, "a.jpg", 300);
        var files = new FakeAttachmentFileInventory();
        files.Put(DocumentCategory.PurchaseDocument, BusinessA, "a.jpg", 300);

        var report = await RunAsync(files, apply: false);

        Assert.Equal(0, report.ExitCode);
        var business = Assert.Single(report.Businesses);
        Assert.Equal(BusinessA, business.BusinessId);
        Assert.Equal(1, business.Attachments);
        Assert.Equal(300, business.ChargedBytes);
        Assert.Empty(business.Findings);
        Assert.NotEqual(0, purchaseId);
    }

    /// <summary>
    /// The three disagreements, per business, in one run: a record whose file is missing, a record
    /// whose size was never recorded, and a stored file no record refers to.
    /// </summary>
    [Fact]
    public async Task A_dry_run_reports_missing_files_unknown_sizes_and_orphans_per_business()
    {
        await SeedPurchaseAsync(BusinessA, "missing.jpg", 300);
        var expenseId = await SeedExpenseAsync(BusinessA, "unsized.pdf", sizeBytes: null);
        await SeedPurchaseAsync(BusinessB, "b.jpg", 100);

        var files = new FakeAttachmentFileInventory();
        files.Put(DocumentCategory.ExpenseAttachment, BusinessA, "unsized.pdf", 450);
        files.Put(DocumentCategory.PurchaseDocument, BusinessA, "orphan.jpg", 700);
        files.Put(DocumentCategory.PurchaseDocument, BusinessB, "b.jpg", 100);

        var report = await RunAsync(files, apply: false);

        Assert.True(report.DryRun);
        Assert.Equal(1, report.ExitCode);
        Assert.Equal(1, report.MissingFiles);
        Assert.Equal(1, report.UnknownSizes);
        Assert.Equal(0, report.FilledSizes);
        Assert.Equal(1, report.OrphanFiles);

        var first = report.Businesses.Single(business => business.BusinessId == BusinessA);
        Assert.Equal(700, first.OrphanBytes);
        Assert.Contains(
            first.Findings,
            finding => finding.Kind == AttachmentStorageFindingKind.MissingFile
                && finding.StoredFileName == "missing.jpg");
        Assert.Contains(
            first.Findings,
            finding => finding.Kind == AttachmentStorageFindingKind.UnknownSize
                && finding.RecordId == expenseId);

        // The other business is reported on its own terms, with nothing of the first one's in it.
        var second = report.Businesses.Single(business => business.BusinessId == BusinessB);
        Assert.Empty(second.Findings);
        Assert.Equal(100, second.ChargedBytes);

        // And a dry run wrote nothing at all.
        await using var verify = TestAppDbContext.Unrestricted(Options());
        Assert.Null((await verify.OperatingExpenses.AsNoTracking().SingleAsync()).AttachmentFileSizeBytes);
    }

    /// <summary>
    /// An apply fills a size in from the stored file - which is the one thing it writes - and
    /// rerunning it finds nothing left to do. Nothing is deleted in either run.
    ///
    /// <para>The unrecorded size here is an <em>expense</em> attachment, because that is the only
    /// kind this schema allows: a purchase's four document values are all present or all absent
    /// under the <c>CK_Receipts_DocumentMetadataAllOrNothing</c> constraint of issue #976, so a
    /// purchase can never refer to a document whose size nobody recorded. The reconciliation still
    /// reads both tables the same way - a constraint is the database's guarantee today, not a
    /// reason for the operator's read to be able to see only half the picture.</para>
    /// </summary>
    [Fact]
    public async Task An_apply_fills_unknown_sizes_from_storage_and_is_idempotent()
    {
        var expenseId = await SeedExpenseAsync(BusinessA, "unsized.pdf", sizeBytes: null);
        await SeedPurchaseAsync(BusinessA, "sized.jpg", 250);
        var files = new FakeAttachmentFileInventory();
        files.Put(DocumentCategory.ExpenseAttachment, BusinessA, "unsized.pdf", 450);
        files.Put(DocumentCategory.PurchaseDocument, BusinessA, "sized.jpg", 250);

        var first = await RunAsync(files, apply: true);

        Assert.False(first.DryRun);
        Assert.Equal(1, first.FilledSizes);
        Assert.Equal(0, first.UnknownSizes);
        Assert.Equal(0, first.ExitCode);

        await using (var verify = TestAppDbContext.Unrestricted(Options()))
        {
            Assert.Equal(
                450,
                (await verify.OperatingExpenses.AsNoTracking().SingleAsync(x => x.Id == expenseId))
                    .AttachmentFileSizeBytes);
        }

        var second = await RunAsync(files, apply: true);

        Assert.Equal(0, second.FilledSizes);
        Assert.Equal(0, second.ExitCode);
        Assert.Equal(700, second.Businesses.Single().ChargedBytes);
        Assert.Empty(files.Deletes);
    }

    /// <summary>
    /// A size that cannot be read from storage either stays unknown and is reported as a missing
    /// file: the reconciliation never guesses a number, and an apply that cannot measure writes
    /// nothing.
    /// </summary>
    [Fact]
    public async Task An_unknown_size_whose_file_is_missing_is_reported_and_never_guessed()
    {
        var expenseId = await SeedExpenseAsync(BusinessA, "gone.pdf", sizeBytes: null);

        var report = await RunAsync(new FakeAttachmentFileInventory(), apply: true);

        Assert.Equal(1, report.MissingFiles);
        Assert.Equal(0, report.FilledSizes);
        Assert.Equal(1, report.ExitCode);

        await using var verify = TestAppDbContext.Unrestricted(Options());
        Assert.Null((await verify.OperatingExpenses.AsNoTracking().SingleAsync(x => x.Id == expenseId))
            .AttachmentFileSizeBytes);
    }

    /// <summary>
    /// On a storage layout that cannot say whose a file is - the filesystem adapter, one folder per
    /// category for every business - an orphan is reported on its own rather than charged to a
    /// business that may not own it.
    /// </summary>
    [Fact]
    public async Task An_orphan_no_layout_can_attribute_is_reported_separately_and_charged_to_nobody()
    {
        await SeedPurchaseAsync(BusinessA, "a.jpg", 300);
        var files = new FakeAttachmentFileInventory();
        files.Put(DocumentCategory.PurchaseDocument, BusinessA, "a.jpg", 300);
        files.Put(DocumentCategory.PurchaseDocument, businessId: null, "orphan.jpg", 900);

        var report = await RunAsync(files, apply: true);

        var orphan = Assert.Single(report.UnattributedOrphans);
        Assert.Equal("orphan.jpg", orphan.StoredFileName);
        Assert.Equal(900, orphan.ByteLength);
        Assert.Equal(1, report.ExitCode);
        Assert.Equal(300, report.Businesses.Single().ChargedBytes);
        Assert.Empty(files.Deletes);
    }

    private async Task<AttachmentStorageReconciliationReport> RunAsync(
        IAttachmentFileInventory files, bool apply)
    {
        await using var db = TestAppDbContext.Unrestricted(Options());
        return await AttachmentStorageReconciler.RunAsync(db, files, apply, CancellationToken.None);
    }

    private async Task<int> SeedPurchaseAsync(int businessId, string storedFileName, long? sizeBytes)
    {
        await using var db = TestAppDbContext.Unrestricted(Options());
        var purchase = new Purchase
        {
            BusinessId = businessId,
            Title = "Restock",
            PurchaseDate = Now,
            FileName = "receipt.jpg",
            StoredFileName = storedFileName,
            ContentType = "image/jpeg",
            FileSizeBytes = sizeBytes,
        };
        db.Receipts.Add(purchase);
        await db.SaveChangesAsync();
        return purchase.Id;
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

    private static Business Business(int id, string name) => new()
    {
        Id = id,
        Name = name,
        Status = BusinessStatus.Active,
        CreatedAtUtc = Now,
    };

    private DbContextOptions<AppDbContext> Options() =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connectionString).Options;

    /// <summary>
    /// An in-memory stand-in for what storage holds, with both layouts the real adapters have: one
    /// that attributes a file to a business (Blob) and one that cannot (the filesystem). It records
    /// nothing but reads - it has no delete at all, which is what makes "the reconciliation never
    /// deletes" a property of the seam rather than a promise - and <see cref="Deletes"/> stays empty
    /// because there is nothing that could fill it.
    /// </summary>
    private sealed class FakeAttachmentFileInventory : IAttachmentFileInventory
    {
        private readonly List<StoredAttachmentFile> _files = [];

        /// <summary>Always empty: this seam offers no way to delete a file.</summary>
        public IReadOnlyList<string> Deletes => [];

        public void Put(DocumentCategory category, int? businessId, string storedFileName, long byteLength) =>
            _files.Add(new StoredAttachmentFile(category, businessId, storedFileName, byteLength));

        public Task<IReadOnlyList<StoredAttachmentFile>> ListAsync(
            DocumentCategory category, IReadOnlyList<int> businessIds, CancellationToken cancellationToken)
        {
            IReadOnlyList<StoredAttachmentFile> listed =
            [
                .. _files.Where(file =>
                    file.Category == category
                    && (file.BusinessId is null || businessIds.Contains(file.BusinessId.Value))),
            ];

            return Task.FromResult(listed);
        }

        public Task<long?> MeasureAsync(
            DocumentCategory category, int businessId, string storedFileName, CancellationToken cancellationToken) =>
            Task.FromResult(_files
                .Where(file =>
                    file.Category == category
                    && string.Equals(file.StoredFileName, storedFileName, StringComparison.Ordinal)
                    && (file.BusinessId is null || file.BusinessId == businessId))
                .Select(file => (long?)file.ByteLength)
                .FirstOrDefault());
    }
}
