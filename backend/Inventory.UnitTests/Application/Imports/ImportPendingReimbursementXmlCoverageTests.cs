using Inventory.Application.Imports;
using Inventory.Application.Time;
using Inventory.Domain.Providers;
using InventoryApi.Tests.Application.Providers;
using Xunit;

namespace InventoryApi.Tests.Application.Imports;

/// <summary>
/// Issue #730 (parent #329 decision P9): what a settlement import records as covered, and when it
/// records nothing.
///
/// Unlike a transaction export, a reimbursement file declares its own period - the provider's
/// <c>ReimbursementStartDate</c>/<c>ReimbursementEndDate</c> - so that period is what is recorded,
/// never the payout date and never the import instant (AGENTS.md § Reimbursements and
/// reconciliation). A file that was not committed records nothing.
/// </summary>
public class ImportPendingReimbursementXmlCoverageTests
{
    private static readonly DateTime ImportedAt = new(2026, 9, 2, 6, 0, 0, DateTimeKind.Utc);

    private static async Task<FakeProviderDataCoverageStore> ImportAsync(FakeSource source, FakeStore store)
    {
        var coverage = new FakeProviderDataCoverageStore();
        await new ImportPendingReimbursementXmlFiles(source, store, new FixedClock(ImportedAt), coverage)
            .Handle(CancellationToken.None);
        return coverage;
    }

    private static PendingReimbursementXmlFile File(string hash, params (DateTime? Start, DateTime? End)[] periods) =>
        new("august.xml", hash, periods.Select(period => new ImportedReimbursementFacts
        {
            ReimbursementStartDate = period.Start,
            ReimbursementEndDate = period.End,
            ReimbursementPayoutDate = new DateTime(2026, 9, 1),
        }).ToList());

    [Fact]
    public async Task A_committed_file_claims_the_reimbursement_period_it_declares()
    {
        var source = new FakeSource();
        source.Add("august.xml", File("hash-a",
            (new DateTime(2026, 8, 1), new DateTime(2026, 8, 15)),
            (new DateTime(2026, 8, 16), new DateTime(2026, 8, 31))));

        var coverage = await ImportAsync(source, new FakeStore());

        var recorded = Assert.Single(coverage.Recorded);
        Assert.Equal(ExternalProvider.Nayax, recorded.Provider);
        Assert.Equal(ProviderDataSource.SettlementImport, recorded.Source);
        Assert.Equal(new DateTime(2026, 8, 1), recorded.FromUtc);
        // The end date is an inclusive calendar day, so the exclusive end is the following midnight.
        Assert.Equal(new DateTime(2026, 9, 1), recorded.ToUtc);
    }

    [Fact]
    public async Task A_file_that_declares_no_period_claims_nothing_rather_than_its_payout_or_import_date()
    {
        var source = new FakeSource();
        source.Add("august.xml", File("hash-a", (null, null)));

        var coverage = await ImportAsync(source, new FakeStore());

        Assert.Empty(coverage.Recorded);
    }

    [Fact]
    public async Task A_duplicate_file_is_skipped_and_claims_nothing_a_second_time()
    {
        var source = new FakeSource();
        source.Add("august.xml", File("hash-a", (new DateTime(2026, 8, 1), new DateTime(2026, 8, 31))));
        var store = new FakeStore();
        store.KnownHashes.Add("hash-a");

        var coverage = await ImportAsync(source, store);

        Assert.Empty(coverage.Recorded);
    }

    [Fact]
    public async Task A_file_that_could_not_be_read_claims_nothing()
    {
        var source = new FakeSource();
        source.Add("august.xml", unreadable: true);

        var coverage = await ImportAsync(source, new FakeStore());

        Assert.Empty(coverage.Recorded);
    }

    [Fact]
    public async Task A_persistence_failure_part_way_through_leaves_no_coverage_for_the_uncommitted_file()
    {
        var source = new FakeSource();
        source.Add("august.xml", File("hash-a", (new DateTime(2026, 8, 1), new DateTime(2026, 8, 31))));
        var coverage = new FakeProviderDataCoverageStore();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ImportPendingReimbursementXmlFiles(
                    source, new FakeStore { ImportThrows = true }, new FixedClock(ImportedAt), coverage)
                .Handle(CancellationToken.None));

        Assert.Empty(coverage.Recorded);
    }

    [Fact]
    public async Task Each_committed_file_records_its_own_period()
    {
        var source = new FakeSource();
        source.Add("july.xml", File("hash-a", (new DateTime(2026, 7, 1), new DateTime(2026, 7, 31))));
        source.Add("august.xml", File("hash-b", (new DateTime(2026, 8, 1), new DateTime(2026, 8, 31))));

        var coverage = await ImportAsync(source, new FakeStore());

        Assert.Equal(
            [(new DateTime(2026, 7, 1), new DateTime(2026, 8, 1)), (new DateTime(2026, 8, 1), new DateTime(2026, 9, 1))],
            coverage.Recorded.Select(x => (x.FromUtc, x.ToUtc)).ToArray());
    }

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    private sealed class FakeSource : IPendingReimbursementXmlSource
    {
        private readonly List<string> _pending = [];
        private readonly Dictionary<string, PendingReimbursementXmlFile?> _files = [];

        public void Add(string fileName, PendingReimbursementXmlFile? file = null, bool unreadable = false)
        {
            _pending.Add(fileName);
            _files[fileName] = unreadable ? null : file;
        }

        public IReadOnlyList<string> ListPendingFiles() => _pending;

        public Task<PendingReimbursementXmlFile?> ReadAsync(string fileName, CancellationToken cancellationToken) =>
            Task.FromResult(_files[fileName] is { } file ? file with { FileName = fileName } : null);

        public bool TryDiscard(string fileName) => true;
    }

    private sealed class FakeStore : IImportedReimbursementStore
    {
        public HashSet<string> KnownHashes { get; } = [];
        public bool ImportThrows { get; init; }

        public Task<bool> HasFileWithContentHashAsync(string contentHash, CancellationToken cancellationToken) =>
            Task.FromResult(KnownHashes.Contains(contentHash));

        public Task ImportAsync(
            PendingReimbursementXmlFile file, DateTime importedAtUtc, CancellationToken cancellationToken) =>
            ImportThrows
                ? throw new InvalidOperationException("the database is unavailable")
                : Task.CompletedTask;
    }
}
