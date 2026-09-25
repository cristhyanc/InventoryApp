using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Storage.Blobs.Models;
using Inventory.Infrastructure.Documents;
using Inventory.Infrastructure.Documents.Migration;
using InventoryApi.Bootstrap;
using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.Tests.Infrastructure.Documents;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Bootstrap;

/// <summary>
/// The filesystem-to-Blob document migration (issue #39, checkpoint 3).
///
/// These tests run the real engine against a real relational database, real filesystem storage
/// over temporary folders, and the real Azure destination over an in-memory container - so the
/// key construction, the protected-over-legacy source preference and the create-if-absent
/// semantics are all the production ones. Only the SDK call itself is stood in for, which is
/// what keeps the suite runnable without Azure credentials.
///
/// The properties worth protecting here are the destructive ones: a migration that guessed an
/// owner would hand one business's document to another, and one that overwrote a destination
/// would destroy a document nobody asked it to touch. Business A is 1 and business B is 2,
/// matching the other tenancy tests.
/// </summary>
public sealed class DocumentMigratorTests : IDisposable
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly string _contentRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly string _webRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly FileSystemDocumentStorage _source;
    private readonly InMemoryDocumentBlobContainer _container = new();
    private readonly AzureBlobMigrationDestination _destination;

    public DocumentMigratorTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
        setup.Businesses.AddRange(
            new Business { Id = BusinessA, Name = "Vending A", CreatedAtUtc = DateTime.UtcNow },
            new Business { Id = BusinessB, Name = "Vending B", CreatedAtUtc = DateTime.UtcNow });
        setup.SaveChanges();

        _source = new FileSystemDocumentStorage(new FileSystemDocumentStorageOptions
        {
            ContentRootPath = _contentRoot,
            WebRootPath = _webRoot,
        });
        _destination = new AzureBlobMigrationDestination(_container);
    }

    public void Dispose()
    {
        _connection.Dispose();
        if (Directory.Exists(_contentRoot)) Directory.Delete(_contentRoot, recursive: true);
        if (Directory.Exists(_webRoot)) Directory.Delete(_webRoot, recursive: true);
    }

    #region Dry run

    /// <summary>
    /// A dry run is the thing an operator runs first, on production, before deciding. It has to
    /// be inert: not "writes nothing important", but reaches the destination only to look.
    /// </summary>
    [Fact]
    public async Task A_dry_run_writes_nothing_to_the_destination()
    {
        var purchase = SeedPurchase(BusinessA, ProtectedDocument("receipt.jpg", "purchase bytes"));

        var report = await RunAsync(apply: false);

        Assert.Equal(1, report.Candidates);
        Assert.Equal(1, report.Pending);
        Assert.Equal(0, report.Migrated);
        Assert.Empty(_container.BlobNames);
        Assert.Equal(0, _container.CreateAttempts);
        Assert.Equal(0, _container.DeleteAttempts);
        Assert.Equal(0, report.ExitCode);

        var item = Assert.Single(report.Items);
        Assert.Equal(DocumentMigrationStatus.Pending, item.Status);
        Assert.Equal(purchase.Id, item.RecordId);
        Assert.Equal($"tenants/{BusinessA}/purchases/{purchase.StoredFileName}", item.DestinationKey);
    }

    /// <summary>
    /// A dry run still leaves the source alone - it is the one guarantee that makes running it
    /// against production uncontroversial.
    /// </summary>
    [Fact]
    public async Task A_dry_run_leaves_the_source_document_in_place()
    {
        var storedFileName = ProtectedDocument("receipt.jpg", "purchase bytes");
        SeedPurchase(BusinessA, storedFileName);

        await RunAsync(apply: false);

        Assert.True(File.Exists(ProtectedPath("receipts", storedFileName)));
    }

    #endregion

    #region Apply

    [Fact]
    public async Task An_apply_copies_an_absent_document_and_verifies_it_at_the_destination()
    {
        var purchase = SeedPurchase(BusinessA, ProtectedDocument("receipt.jpg", "purchase bytes"));

        var report = await RunAsync(apply: true);

        Assert.Equal(1, report.Migrated);
        Assert.Equal(0, report.ExitCode);

        var key = $"tenants/{BusinessA}/purchases/{purchase.StoredFileName}";
        Assert.Equal([key], _container.BlobNames);
        Assert.Equal("purchase bytes", Encoding.UTF8.GetString(_container[key]));
    }

    /// <summary>
    /// The migration's whole claim is that the copy is the same document. Size alone would not
    /// settle it, so the bytes are compared by hash - here independently of the engine's own
    /// comparison, against what actually landed at the destination.
    /// </summary>
    [Fact]
    public async Task The_copied_document_has_the_same_length_and_sha256_as_its_source()
    {
        var storedFileName = ProtectedDocument("receipt.jpg", "purchase bytes");
        var purchase = SeedPurchase(BusinessA, storedFileName);

        var report = await RunAsync(apply: true);

        var written = _container[$"tenants/{BusinessA}/purchases/{purchase.StoredFileName}"];
        var sourceBytes = await File.ReadAllBytesAsync(ProtectedPath("receipts", storedFileName));

        Assert.Equal(sourceBytes.Length, written.Length);
        Assert.Equal(Sha256(sourceBytes), Sha256(written));
        Assert.Equal(sourceBytes.Length, Assert.Single(report.Items).ByteLength);
    }

    [Fact]
    public async Task An_expense_attachment_is_copied_under_the_expenses_prefix()
    {
        var expense = SeedExpense(BusinessB, ProtectedDocument("invoice.pdf", "expense bytes", "expenses"));

        var report = await RunAsync(apply: true);

        Assert.Equal(1, report.Migrated);
        Assert.Equal(
            [$"tenants/{BusinessB}/expenses/{expense.AttachmentStoredFileName}"],
            _container.BlobNames);
    }

    /// <summary>
    /// Two businesses may hold the same server-generated stored name. They are two documents,
    /// and the only thing that separates them is the prefix read from each record's own owner.
    /// </summary>
    [Fact]
    public async Task Two_businesses_sharing_a_stored_name_migrate_to_separate_destinations()
    {
        var storedFileName = $"{Guid.NewGuid()}.jpg";
        WriteProtected("receipts", storedFileName, "A's document");
        SeedPurchase(BusinessA, storedFileName);

        // The same name for business B, which on a filesystem could only ever be one file; the
        // migration must still produce two independent blobs.
        SeedPurchase(BusinessB, storedFileName);

        var report = await RunAsync(apply: true);

        Assert.Equal(2, report.Migrated);
        Assert.Equal(
            [$"tenants/{BusinessA}/purchases/{storedFileName}", $"tenants/{BusinessB}/purchases/{storedFileName}"],
            _container.BlobNames.OrderBy(name => name, StringComparer.Ordinal));
    }

    /// <summary>
    /// Nothing about a successful migration may touch the filesystem. Retiring the source copies
    /// is a separate human decision, and this checkpoint deliberately has no way to make it.
    /// </summary>
    [Fact]
    public async Task An_apply_never_deletes_or_alters_the_source_document()
    {
        var storedFileName = ProtectedDocument("receipt.jpg", "purchase bytes");
        SeedPurchase(BusinessA, storedFileName);
        var path = ProtectedPath("receipts", storedFileName);
        var before = await File.ReadAllBytesAsync(path);

        await RunAsync(apply: true);

        Assert.True(File.Exists(path));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Equal(0, _container.DeleteAttempts);
    }

    /// <summary>
    /// The migration reads the database and must not write to it: the stored name is the link
    /// between record and document, and moving bytes is not a reason to change it.
    /// </summary>
    [Fact]
    public async Task An_apply_does_not_change_the_document_metadata_on_the_record()
    {
        var storedFileName = ProtectedDocument("receipt.jpg", "purchase bytes");
        var purchase = SeedPurchase(BusinessA, storedFileName);

        await RunAsync(apply: true);

        await using var db = TestAppDbContext.Unrestricted(_options);
        var persisted = await db.Receipts.AsNoTracking().SingleAsync(x => x.Id == purchase.Id);
        Assert.Equal(storedFileName, persisted.StoredFileName);
        Assert.Equal("receipt.jpg", persisted.FileName);
        Assert.Equal(BusinessA, persisted.BusinessId);
    }

    #endregion

    #region Restartability

    /// <summary>
    /// The second run of a finished migration is the one an operator will actually repeat, after
    /// an interruption or just to check. It must copy nothing and must not report the work as
    /// outstanding.
    /// </summary>
    [Fact]
    public async Task A_second_run_reports_already_present_and_copies_nothing()
    {
        SeedPurchase(BusinessA, ProtectedDocument("receipt.jpg", "purchase bytes"));
        SeedExpense(BusinessB, ProtectedDocument("invoice.pdf", "expense bytes", "expenses"));

        var first = await RunAsync(apply: true);
        Assert.Equal(2, first.Migrated);
        var attemptsAfterFirst = _container.CreateAttempts;

        var second = await RunAsync(apply: true);

        Assert.Equal(0, second.Migrated);
        Assert.Equal(2, second.AlreadyPresent);
        Assert.Equal(0, second.ExitCode);
        Assert.Equal(2, _container.BlobNames.Count);
        Assert.Equal(attemptsAfterFirst, _container.CreateAttempts);
    }

    [Fact]
    public async Task A_destination_that_already_holds_the_same_bytes_is_not_uploaded_again()
    {
        var storedFileName = ProtectedDocument("receipt.jpg", "purchase bytes");
        var purchase = SeedPurchase(BusinessA, storedFileName);
        _container.Put(
            $"tenants/{BusinessA}/purchases/{purchase.StoredFileName}",
            Encoding.UTF8.GetBytes("purchase bytes"));

        var report = await RunAsync(apply: true);

        Assert.Equal(1, report.AlreadyPresent);
        Assert.Equal(0, _container.CreateAttempts);
    }

    #endregion

    #region Collisions

    /// <summary>
    /// The dangerous case: something is already at the destination and it is not this document.
    /// Size is equal, so only the hash separates them - which is exactly why the comparison
    /// cannot be by size or timestamp.
    /// </summary>
    [Fact]
    public async Task A_destination_of_the_same_size_but_different_bytes_is_a_collision()
    {
        var purchase = SeedPurchase(BusinessA, ProtectedDocument("receipt.jpg", "purchase bytes"));
        var key = $"tenants/{BusinessA}/purchases/{purchase.StoredFileName}";
        _container.Put(key, Encoding.UTF8.GetBytes("PURCHASE BYTES"));

        var report = await RunAsync(apply: true);

        Assert.Equal(1, report.Collision);
        Assert.Equal(0, report.Migrated);
        Assert.Equal(1, report.ExitCode);

        // Untouched: not overwritten, not renamed, not deleted.
        Assert.Equal("PURCHASE BYTES", Encoding.UTF8.GetString(_container[key]));
        Assert.Equal(0, _container.CreateAttempts);
        Assert.Equal(0, _container.DeleteAttempts);
    }

    [Fact]
    public async Task A_destination_of_a_different_size_is_a_collision()
    {
        var purchase = SeedPurchase(BusinessA, ProtectedDocument("receipt.jpg", "purchase bytes"));
        var key = $"tenants/{BusinessA}/purchases/{purchase.StoredFileName}";
        _container.Put(key, Encoding.UTF8.GetBytes("a different document entirely"));

        var report = await RunAsync(apply: true);

        Assert.Equal(1, report.Collision);
        Assert.Equal("a different document entirely", Encoding.UTF8.GetString(_container[key]));
    }

    /// <summary>
    /// A dry run reports the collision before anything has been written, which is the point of
    /// running one: the operator finds out while it is still cheap to investigate.
    /// </summary>
    [Fact]
    public async Task A_dry_run_reports_a_collision_and_exits_non_zero()
    {
        var purchase = SeedPurchase(BusinessA, ProtectedDocument("receipt.jpg", "purchase bytes"));
        _container.Put(
            $"tenants/{BusinessA}/purchases/{purchase.StoredFileName}",
            Encoding.UTF8.GetBytes("PURCHASE BYTES"));

        var report = await RunAsync(apply: false);

        Assert.Equal(1, report.Collision);
        Assert.Equal(1, report.ExitCode);
        Assert.Equal(0, _container.CreateAttempts);
    }

    #endregion

    #region Source resolution

    /// <summary>
    /// Protected storage wins, exactly as it does for a live download. Migrating the legacy copy
    /// of a document that has already been re-uploaded would copy the wrong bytes.
    /// </summary>
    [Fact]
    public async Task A_document_in_both_locations_migrates_the_protected_copy()
    {
        var storedFileName = $"{Guid.NewGuid()}.jpg";
        WriteLegacy("receipts", storedFileName, "legacy bytes");
        WriteProtected("receipts", storedFileName, "protected bytes");
        SeedPurchase(BusinessA, storedFileName);

        var report = await RunAsync(apply: true);

        Assert.Equal(
            "protected bytes",
            Encoding.UTF8.GetString(_container[$"tenants/{BusinessA}/purchases/{storedFileName}"]));
        Assert.Equal(DocumentSourceLocation.ProtectedStorage, Assert.Single(report.Items).Source);
        Assert.Equal(1, report.ProtectedSource);
        Assert.Equal(0, report.LegacySource);
    }

    /// <summary>
    /// The legacy web-root documents are the ones this migration exists for; they are also the
    /// ones whose count decides when the fallback can be retired.
    /// </summary>
    [Fact]
    public async Task A_document_only_in_the_legacy_web_root_migrates_and_is_reported_as_legacy()
    {
        var storedFileName = $"{Guid.NewGuid()}.pdf";
        WriteLegacy("expenses", storedFileName, "legacy attachment");
        var expense = SeedExpense(BusinessB, storedFileName);

        var report = await RunAsync(apply: true);

        Assert.Equal(1, report.Migrated);
        Assert.Equal(
            "legacy attachment",
            Encoding.UTF8.GetString(_container[$"tenants/{BusinessB}/expenses/{expense.AttachmentStoredFileName}"]));
        Assert.Equal(DocumentSourceLocation.LegacyWebRoot, Assert.Single(report.Items).Source);
        Assert.Equal(1, report.LegacySource);
    }

    /// <summary>
    /// Metadata pointing at a document that is not there is a real state in this data, and the
    /// one thing that must not happen is an empty blob being created to satisfy it.
    /// </summary>
    [Fact]
    public async Task A_record_whose_document_is_missing_is_reported_and_nothing_is_written()
    {
        var purchase = SeedPurchase(BusinessA, $"{Guid.NewGuid()}.jpg");

        var report = await RunAsync(apply: true);

        Assert.Equal(1, report.MissingSource);
        Assert.Equal(1, report.ExitCode);
        Assert.Empty(_container.BlobNames);
        Assert.Equal(0, _container.CreateAttempts);

        var item = Assert.Single(report.Items);
        Assert.Equal(purchase.Id, item.RecordId);
        Assert.Null(item.Source);

        await using var db = TestAppDbContext.Unrestricted(_options);
        Assert.Equal(
            purchase.StoredFileName,
            (await db.Receipts.AsNoTracking().SingleAsync(x => x.Id == purchase.Id)).StoredFileName);
    }

    [Fact]
    public async Task A_record_with_no_stored_document_is_not_a_candidate()
    {
        SeedPurchase(BusinessA, storedFileName: string.Empty);
        SeedExpense(BusinessB, storedFileName: null);

        var report = await RunAsync(apply: true);

        Assert.Equal(0, report.Candidates);
        Assert.Empty(_container.BlobNames);
    }

    #endregion

    #region Ownership

    /// <summary>
    /// A record with no usable owner has no safe destination. Filing it under any business would
    /// hand a document to one that does not own it, so it is refused and reported instead - and
    /// the run exits non-zero so it cannot be mistaken for finished.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_record_with_an_unusable_business_id_is_refused_rather_than_migrated(int businessId)
    {
        var purchase = SeedPurchase(businessId, ProtectedDocument("receipt.jpg", "purchase bytes"));

        var report = await RunAsync(apply: true);

        Assert.Equal(1, report.InvalidBusiness);
        Assert.Equal(0, report.Migrated);
        Assert.Equal(1, report.ExitCode);
        Assert.Empty(_container.BlobNames);
        Assert.Equal(0, _container.CreateAttempts);

        var item = Assert.Single(report.Items);
        Assert.Equal(purchase.Id, item.RecordId);
        Assert.Equal(businessId, item.BusinessId);
        Assert.Null(item.DestinationKey);
    }

    /// <summary>
    /// One unusable record must not stop the rest: the operator needs the whole picture from one
    /// run, not one failure at a time.
    /// </summary>
    [Fact]
    public async Task An_unusable_record_does_not_stop_the_others()
    {
        SeedPurchase(0, ProtectedDocument("orphan.jpg", "orphan bytes"));
        var owned = SeedPurchase(BusinessA, ProtectedDocument("receipt.jpg", "purchase bytes"));

        var report = await RunAsync(apply: true);

        Assert.Equal(2, report.Candidates);
        Assert.Equal(1, report.InvalidBusiness);
        Assert.Equal(1, report.Migrated);
        Assert.Equal([$"tenants/{BusinessA}/purchases/{owned.StoredFileName}"], _container.BlobNames);
    }

    #endregion

    #region Duplicate references

    /// <summary>
    /// Two records of one business naming the same stored document resolve to one destination.
    /// The bytes are copied once, and the second record is still listed: silently collapsing the
    /// two would hide a data problem worth knowing about.
    /// </summary>
    [Fact]
    public async Task Two_records_naming_the_same_document_migrate_it_once_and_both_are_reported()
    {
        var storedFileName = ProtectedDocument("receipt.jpg", "purchase bytes");
        var first = SeedPurchase(BusinessA, storedFileName);
        var second = SeedPurchase(BusinessA, storedFileName);

        var report = await RunAsync(apply: true);

        Assert.Equal(2, report.Candidates);
        Assert.Equal(1, report.Migrated);
        Assert.Equal(1, report.DuplicateReferences);
        Assert.Equal(1, _container.CreateAttempts);
        Assert.Single(_container.BlobNames);

        // Deterministic: the lower record id owns the destination, the later one is the duplicate.
        Assert.Equal(
            DocumentMigrationStatus.Migrated,
            report.Items.Single(item => item.RecordId == first.Id).Status);
        Assert.Equal(
            DocumentMigrationStatus.DuplicateReference,
            report.Items.Single(item => item.RecordId == second.Id).Status);
        Assert.Equal(0, report.ExitCode);
    }

    /// <summary>
    /// The same stored name under different categories is two documents, not a duplicate: they
    /// have different destinations and both must be copied.
    /// </summary>
    [Fact]
    public async Task The_same_stored_name_in_two_categories_is_two_documents()
    {
        var storedFileName = $"{Guid.NewGuid()}.pdf";
        WriteProtected("receipts", storedFileName, "purchase bytes");
        WriteProtected("expenses", storedFileName, "expense bytes");
        SeedPurchase(BusinessA, storedFileName);
        SeedExpense(BusinessA, storedFileName);

        var report = await RunAsync(apply: true);

        Assert.Equal(2, report.Migrated);
        Assert.Equal(0, report.DuplicateReferences);
        Assert.Equal(
            [$"tenants/{BusinessA}/expenses/{storedFileName}", $"tenants/{BusinessA}/purchases/{storedFileName}"],
            _container.BlobNames.OrderBy(name => name, StringComparer.Ordinal));
    }

    #endregion

    #region Storage failures

    /// <summary>
    /// A missing container is a misconfigured destination, not an empty one. If it read as "no
    /// document" the migration would cheerfully report every document as copied while writing to
    /// storage that does not exist.
    /// </summary>
    [Fact]
    public async Task A_missing_container_fails_the_run_rather_than_looking_like_an_empty_destination()
    {
        SeedPurchase(BusinessA, ProtectedDocument("receipt.jpg", "purchase bytes"));
        _container.FailReadsWith = new RequestFailedException(
            404, "container missing", BlobErrorCode.ContainerNotFound.ToString(), null);

        var failure = await Assert.ThrowsAsync<RequestFailedException>(() => RunAsync(apply: true));

        Assert.Equal(BlobErrorCode.ContainerNotFound.ToString(), failure.ErrorCode);
        Assert.Equal(0, _container.CreateAttempts);
    }

    [Fact]
    public async Task An_authorization_failure_fails_the_run()
    {
        SeedPurchase(BusinessA, ProtectedDocument("receipt.jpg", "purchase bytes"));
        _container.FailReadsWith = new RequestFailedException(
            403, "denied", "AuthorizationPermissionMismatch", null);

        await Assert.ThrowsAsync<RequestFailedException>(() => RunAsync(apply: true));
    }

    /// <summary>
    /// A failed upload aborts the whole run rather than becoming that document's
    /// <see cref="DocumentMigrationStatus.Failed"/>.
    ///
    /// This is a deliberate contract, not an omission. The failures an upload can raise -
    /// container missing, authorization refused, account disabled, service unavailable - are
    /// almost never about the one document being copied; they are about the destination. Turning
    /// them into a per-document result would produce the most misleading report this command
    /// could print: "32 migrated, 3 failed", when in truth nothing could have worked and the
    /// three are simply where it stopped. Classifying them per item would also mean a second
    /// error-code taxonomy, which is exactly the mistake checkpoint 2 was fixed for.
    ///
    /// Recovery is the same in every case: fix the destination and run the command again. That
    /// is safe because the migration is idempotent - documents already copied come back as
    /// <see cref="DocumentMigrationStatus.AlreadyPresent"/> - and because a failed run has
    /// changed nothing on the source side, which the assertions below pin down.
    /// </summary>
    [Theory]
    [InlineData(404, "ContainerNotFound")]
    [InlineData(403, "AuthorizationPermissionMismatch")]
    [InlineData(403, "AccountIsDisabled")]
    [InlineData(503, "ServerBusy")]
    public async Task A_failed_upload_aborts_the_run_and_leaves_the_source_untouched(
        int status, string errorCode)
    {
        var storedFileName = ProtectedDocument("receipt.jpg", "purchase bytes");
        var purchase = SeedPurchase(BusinessA, storedFileName);
        var path = ProtectedPath("receipts", storedFileName);
        var sourceBefore = await File.ReadAllBytesAsync(path);
        _container.FailCreatesWith = new RequestFailedException(status, "upload failed", errorCode, null);

        var failure = await Assert.ThrowsAsync<RequestFailedException>(() => RunAsync(apply: true));

        Assert.Equal(errorCode, failure.ErrorCode);

        // Nothing was written at the destination, and nothing was taken away from the source.
        Assert.Empty(_container.BlobNames);
        Assert.Equal(0, _container.DeleteAttempts);
        Assert.True(File.Exists(path), "the source document must survive a failed upload.");
        Assert.Equal(sourceBefore, await File.ReadAllBytesAsync(path));
        await AssertRecordUnchangedAsync(purchase, storedFileName);
    }

    /// <summary>
    /// After an aborted run, running the command again finishes the job: this is what makes the
    /// fatal-abort contract above a workable one rather than a dead end.
    /// </summary>
    [Fact]
    public async Task A_run_that_aborted_on_upload_completes_when_it_is_run_again()
    {
        SeedPurchase(BusinessA, ProtectedDocument("receipt.jpg", "purchase bytes"));
        _container.FailCreatesWith = new RequestFailedException(503, "busy", "ServerBusy", null);
        await Assert.ThrowsAsync<RequestFailedException>(() => RunAsync(apply: true));

        _container.FailCreatesWith = null;
        var report = await RunAsync(apply: true);

        Assert.Equal(1, report.Migrated);
        Assert.Equal(0, report.ExitCode);
    }

    #endregion

    #region Verification

    /// <summary>
    /// The case the read-back verification exists for: the destination accepts the write and
    /// then holds something else. Nothing before the verification would notice - the upload
    /// reported success - so without reading the bytes back the migration would report a
    /// document as safely copied when it is not.
    ///
    /// The same-length case is the one that matters most: only the hash separates those two
    /// documents, so a size check alone would pass.
    /// </summary>
    [Theory]
    [InlineData("PURCHASE BYTES")]
    [InlineData("short")]
    public async Task A_copy_that_reads_back_differently_is_reported_as_failed_and_not_migrated(
        string storedInstead)
    {
        var storedFileName = ProtectedDocument("receipt.jpg", "purchase bytes");
        var purchase = SeedPurchase(BusinessA, storedFileName);
        var path = ProtectedPath("receipts", storedFileName);
        var sourceBefore = await File.ReadAllBytesAsync(path);
        _container.CorruptCreatedContentWith = _ => Encoding.UTF8.GetBytes(storedInstead);

        var report = await RunAsync(apply: true);

        Assert.Equal(1, report.Failed);
        Assert.Equal(0, report.Migrated);
        Assert.Equal(1, report.ExitCode);

        var item = Assert.Single(report.Items);
        Assert.Equal(DocumentMigrationStatus.Failed, item.Status);
        Assert.Equal(purchase.Id, item.RecordId);
        Assert.Contains("does not match its source", item.Reason, StringComparison.Ordinal);

        // The source is the only remaining copy of those bytes, so it must be exactly as it was.
        Assert.True(File.Exists(path), "the source document must survive a failed verification.");
        Assert.Equal(sourceBefore, await File.ReadAllBytesAsync(path));
        await AssertRecordUnchangedAsync(purchase, storedFileName);

        // The mismatching destination is left alone too: this command does not delete anything,
        // and what is sitting there is for a human to identify.
        Assert.Equal(0, _container.DeleteAttempts);
        Assert.Equal(
            storedInstead,
            Encoding.UTF8.GetString(_container[$"tenants/{BusinessA}/purchases/{storedFileName}"]));
    }

    /// <summary>
    /// A failed verification must not be quietly resolved by a later run either. The second run
    /// sees a destination that differs from its source, which is a collision - still not
    /// overwritten, still needing a human.
    /// </summary>
    [Fact]
    public async Task Re_running_after_a_failed_verification_reports_a_collision_rather_than_overwriting()
    {
        var storedFileName = ProtectedDocument("receipt.jpg", "purchase bytes");
        SeedPurchase(BusinessA, storedFileName);
        _container.CorruptCreatedContentWith = _ => Encoding.UTF8.GetBytes("PURCHASE BYTES");
        Assert.Equal(1, (await RunAsync(apply: true)).Failed);

        _container.CorruptCreatedContentWith = null;
        var report = await RunAsync(apply: true);

        Assert.Equal(1, report.Collision);
        Assert.Equal(0, report.Migrated);
        Assert.Equal(1, report.ExitCode);
        Assert.Equal(
            "PURCHASE BYTES",
            Encoding.UTF8.GetString(_container[$"tenants/{BusinessA}/purchases/{storedFileName}"]));
    }

    #endregion

    #region Helpers

    private Task<DocumentMigrationReport> RunAsync(bool apply)
    {
        var db = TestAppDbContext.Unrestricted(_options);
        return RunAndDisposeAsync(db, apply);

        async Task<DocumentMigrationReport> RunAndDisposeAsync(AppDbContext context, bool applyChanges)
        {
            await using (context)
            {
                return await DocumentMigrator.RunAsync(
                    context, _source, _destination, applyChanges, CancellationToken.None);
            }
        }
    }

    private static string Sha256(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    /// <summary>
    /// The migration reads the database and never writes to it, so a failure must leave the
    /// record naming exactly the document it named before.
    /// </summary>
    private async Task AssertRecordUnchangedAsync(Purchase purchase, string storedFileName)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);
        var persisted = await db.Receipts.AsNoTracking().SingleAsync(x => x.Id == purchase.Id);

        Assert.Equal(storedFileName, persisted.StoredFileName);
        Assert.Equal("receipt.jpg", persisted.FileName);
        Assert.Equal(purchase.BusinessId, persisted.BusinessId);
    }

    private string ProtectedPath(string folderName, string storedFileName) =>
        Path.Combine(_contentRoot, "protected-files", folderName, storedFileName);

    /// <summary>Writes a document to protected storage and returns its stored name.</summary>
    private string ProtectedDocument(string extensionSource, string content, string folderName = "receipts")
    {
        var storedFileName = $"{Guid.NewGuid()}{Path.GetExtension(extensionSource)}";
        WriteProtected(folderName, storedFileName, content);
        return storedFileName;
    }

    private void WriteProtected(string folderName, string storedFileName, string content)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_contentRoot, "protected-files", folderName));
        File.WriteAllText(Path.Combine(folder.FullName, storedFileName), content);
    }

    private void WriteLegacy(string folderName, string storedFileName, string content)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_webRoot, folderName));
        File.WriteAllText(Path.Combine(folder.FullName, storedFileName), content);
    }

    /// <summary>
    /// Seeds a purchase through an unrestricted context so the business key is exactly what the
    /// test says it is - including the unusable values a scoped write could never produce but
    /// historical data can still hold.
    /// </summary>
    private Purchase SeedPurchase(int businessId, string storedFileName)
    {
        using var db = TestAppDbContext.Unrestricted(_options);
        var purchase = new Purchase
        {
            BusinessId = businessId,
            Title = "Purchase",
            FileName = "receipt.jpg",
            StoredFileName = storedFileName,
            ContentType = "image/jpeg",
            FileSizeBytes = 14,
            PurchaseDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Receipts.Add(purchase);
        db.SaveChanges();
        return purchase;
    }

    private OperatingExpense SeedExpense(int businessId, string? storedFileName)
    {
        using var db = TestAppDbContext.Unrestricted(_options);
        var expense = new OperatingExpense
        {
            BusinessId = businessId,
            Description = "Site power",
            ExpenseDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            AttachmentFileName = "invoice.pdf",
            AttachmentStoredFileName = storedFileName,
            AttachmentContentType = "application/pdf",
            AttachmentFileSizeBytes = 13,
        };
        db.OperatingExpenses.Add(expense);
        db.SaveChanges();
        return expense;
    }

    #endregion
}
