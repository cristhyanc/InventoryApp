using Inventory.Application.Imports;
using Inventory.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.Imports;

/// <summary>
/// <see cref="ImportPendingReimbursementXmlFiles"/> (issue #299) orchestrates the pending
/// reimbursement XML import: it must report imported, skipped and failed files exactly as the
/// former <c>ImportService.ImportPendingXmlFilesAsync</c> did, never import the same bytes
/// twice for one business, and never leave a file it dealt with in the queue.
/// </summary>
public class ImportPendingReimbursementXmlFilesTests
{
    private static readonly DateTime ImportedAt = new(2026, 10, 3, 1, 30, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_new_file_is_persisted_discarded_and_counted_with_its_reimbursements()
    {
        var source = new FakeSource();
        source.Add("august.xml", File("hash-a", reimbursements: 2));
        var store = new FakeStore();

        var result = await Import(source, store);

        Assert.Equal(new ImportedFileImportResult(1, 2, 0, 0), result);
        var imported = Assert.Single(store.Imported);
        Assert.Equal("august.xml", imported.File.FileName);
        Assert.Equal("hash-a", imported.File.ContentHash);
        Assert.Equal(ImportedAt, imported.ImportedAtUtc);
        Assert.Equal(["august.xml"], source.Discarded);
        Assert.Empty(source.Remaining);
    }

    /// <summary>
    /// File-hash idempotency: the same bytes already imported by this business are skipped, not
    /// imported a second time. Double counting an imported reimbursement would corrupt
    /// reconciliation, so this is the invariant the hash exists for.
    /// </summary>
    [Fact]
    public async Task A_file_whose_hash_is_already_imported_is_skipped_and_never_persisted()
    {
        var source = new FakeSource();
        source.Add("august.xml", File("hash-a", reimbursements: 3));
        var store = new FakeStore();
        store.KnownHashes.Add("hash-a");

        var result = await Import(source, store);

        Assert.Equal(new ImportedFileImportResult(0, 0, 1, 0), result);
        Assert.Empty(store.Imported);
        Assert.Equal(["august.xml"], source.Discarded);
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_or_parsed_is_counted_as_failed_and_left_in_the_queue()
    {
        var source = new FakeSource();
        source.Add("broken.xml", unreadable: true);
        var store = new FakeStore();

        var result = await Import(source, store);

        Assert.Equal(new ImportedFileImportResult(0, 0, 0, 1), result);
        Assert.Empty(store.Imported);
        Assert.Empty(source.Discarded);
        Assert.Equal(["broken.xml"], source.Remaining);
    }

    [Fact]
    public async Task Each_file_is_judged_on_its_own_so_one_bad_file_does_not_stop_the_run()
    {
        var source = new FakeSource();
        source.Add("broken.xml", unreadable: true);
        source.Add("august.xml", File("hash-a", reimbursements: 2));
        source.Add("august-again.xml", File("hash-b", reimbursements: 1));
        var store = new FakeStore();
        store.KnownHashes.Add("hash-b");

        var result = await Import(source, store);

        Assert.Equal(new ImportedFileImportResult(1, 2, 1, 1), result);
        Assert.Equal("hash-a", Assert.Single(store.Imported).File.ContentHash);
    }

    /// <summary>
    /// A file that was persisted but could not be removed stays in the queue, so the run must
    /// report it as a failure rather than as an import - while still reporting the reimbursement
    /// rows it genuinely persisted, exactly as the legacy implementation's counters did.
    /// </summary>
    [Fact]
    public async Task A_persisted_file_that_cannot_be_discarded_is_counted_as_failed_with_its_rows()
    {
        var source = new FakeSource { DiscardFails = true };
        source.Add("august.xml", File("hash-a", reimbursements: 2));
        var store = new FakeStore();

        var result = await Import(source, store);

        Assert.Equal(new ImportedFileImportResult(0, 2, 0, 1), result);
        Assert.Single(store.Imported);
    }

    [Fact]
    public async Task A_duplicate_that_cannot_be_discarded_is_counted_as_failed_not_skipped()
    {
        var source = new FakeSource { DiscardFails = true };
        source.Add("august.xml", File("hash-a", reimbursements: 1));
        var store = new FakeStore();
        store.KnownHashes.Add("hash-a");

        var result = await Import(source, store);

        Assert.Equal(new ImportedFileImportResult(0, 0, 0, 1), result);
    }

    [Fact]
    public async Task An_empty_queue_imports_nothing_and_reports_zero_counts()
    {
        var result = await Import(new FakeSource(), new FakeStore());

        Assert.Equal(new ImportedFileImportResult(0, 0, 0, 0), result);
    }

    /// <summary>
    /// A persistence failure is not a "failed file": it propagates, so the operator sees the
    /// real error instead of a count that hides it, and the file stays in the queue.
    /// </summary>
    [Fact]
    public async Task A_persistence_failure_propagates_and_leaves_the_file_in_the_queue()
    {
        var source = new FakeSource();
        source.Add("august.xml", File("hash-a", reimbursements: 1));
        var store = new FakeStore { ImportThrows = true };

        await Assert.ThrowsAsync<InvalidOperationException>(() => Import(source, store));

        Assert.Empty(source.Discarded);
        Assert.Equal(["august.xml"], source.Remaining);
    }

    [Fact]
    public async Task Cancellation_is_passed_through_to_both_ports()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new FakeSource();
        source.Add("august.xml", File("hash-a", reimbursements: 1));
        var store = new FakeStore();

        await new ImportPendingReimbursementXmlFiles(source, store, new FixedClock(ImportedAt))
            .Handle(cancellation.Token);

        Assert.Equal(cancellation.Token, source.ObservedToken);
        Assert.Equal(cancellation.Token, store.ObservedToken);
    }

    private static Task<ImportedFileImportResult> Import(FakeSource source, FakeStore store) =>
        new ImportPendingReimbursementXmlFiles(source, store, new FixedClock(ImportedAt))
            .Handle(CancellationToken.None);

    private static PendingReimbursementXmlFile File(string hash, int reimbursements) =>
        new(
            "ignored.xml",
            hash,
            Enumerable.Range(0, reimbursements)
                .Select(index => new ImportedReimbursementFacts { CustomerId = index.ToString() })
                .ToList());

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    private sealed class FakeSource : IPendingReimbursementXmlSource
    {
        private readonly List<string> _pending = [];
        private readonly Dictionary<string, PendingReimbursementXmlFile?> _files = [];

        public bool DiscardFails { get; init; }
        public List<string> Discarded { get; } = [];
        public CancellationToken? ObservedToken { get; private set; }
        public IReadOnlyList<string> Remaining => _pending.Except(Discarded).ToList();

        public void Add(string fileName, PendingReimbursementXmlFile? file = null, bool unreadable = false)
        {
            _pending.Add(fileName);
            _files[fileName] = unreadable ? null : file;
        }

        public IReadOnlyList<string> ListPendingFiles() => _pending;

        public Task<PendingReimbursementXmlFile?> ReadAsync(string fileName, CancellationToken cancellationToken)
        {
            ObservedToken = cancellationToken;
            var file = _files[fileName];
            // The file name the adapter reports is the file's own, not the caller's copy.
            return Task.FromResult(file is null ? null : file with { FileName = fileName });
        }

        public bool TryDiscard(string fileName)
        {
            if (DiscardFails) return false;
            Discarded.Add(fileName);
            return true;
        }
    }

    private sealed class FakeStore : IImportedReimbursementStore
    {
        public HashSet<string> KnownHashes { get; } = [];
        public bool ImportThrows { get; init; }
        public List<(PendingReimbursementXmlFile File, DateTime ImportedAtUtc)> Imported { get; } = [];
        public CancellationToken? ObservedToken { get; private set; }

        public Task<bool> HasFileWithContentHashAsync(string contentHash, CancellationToken cancellationToken)
        {
            ObservedToken = cancellationToken;
            return Task.FromResult(KnownHashes.Contains(contentHash));
        }

        public Task ImportAsync(
            PendingReimbursementXmlFile file, DateTime importedAtUtc, CancellationToken cancellationToken)
        {
            ObservedToken = cancellationToken;
            if (ImportThrows) throw new InvalidOperationException("The database is unavailable.");

            Imported.Add((file, importedAtUtc));
            KnownHashes.Add(file.ContentHash);
            return Task.CompletedTask;
        }
    }
}
