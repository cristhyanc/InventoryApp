using Inventory.Application.Imports;
using Inventory.Domain.Imports;
using Inventory.Domain.Tenancy;

namespace InventoryApi.Tests.Application.Imports;

/// <summary>
/// The in-memory doubles the uploaded reimbursement report import's Application tests use (issue
/// #1149). Each one is the narrowest thing that can answer the port it stands in for; everything
/// that needs a real database is in the integration project instead.
/// </summary>
internal static class ReimbursementImportFakes
{
    /// <summary>A made-up actor, as a validated token would identify one.</summary>
    public static ActorIdentity Actor(string objectId = "22222222-2222-2222-2222-222222222222")
    {
        ActorIdentity.TryCreate("11111111-1111-1111-1111-111111111111", objectId, out var identity);
        return identity!;
    }

    /// <summary>One parsed file with one declared settlement period.</summary>
    public static PendingReimbursementXmlFile File(string hash, int reimbursements = 1) =>
        new(
            $"{hash}.xml",
            hash,
            Enumerable.Range(0, reimbursements)
                .Select(index => new ImportedReimbursementFacts { CustomerId = index.ToString() })
                .ToList());
}

/// <summary>An archive reader that answers whatever a case was arranged with.</summary>
internal sealed class FakeArchiveReader : IReimbursementArchiveReader
{
    private readonly Func<byte[], ReimbursementArchiveContents> _read;

    private FakeArchiveReader(Func<byte[], ReimbursementArchiveContents> read) => _read = read;

    public List<int> ArchiveLengthsRead { get; } = [];

    public static FakeArchiveReader Refusing(string rejection) =>
        new(_ => ReimbursementArchiveContents.Refused(rejection));

    public static FakeArchiveReader Returning(params ReimbursementArchiveXmlEntry[] entries) =>
        new(_ => ReimbursementArchiveContents.Accepted(entries, 1, 0, 0));

    public static FakeArchiveReader Empty() =>
        new(_ => ReimbursementArchiveContents.Accepted([], 0, 2, 0));

    public ReimbursementArchiveContents Read(byte[] archive)
    {
        ArchiveLengthsRead.Add(archive.Length);
        return _read(archive);
    }
}

/// <summary>
/// An in-memory reimbursement import job store: enough to drive the claim, the per-file outcomes and
/// the three endings without a database.
/// </summary>
internal sealed class FakeReimbursementImportJobStore : IReimbursementImportJobStore
{
    private int _nextId = 1;

    public List<NewReimbursementImportJob> Created { get; } = [];

    public Dictionary<int, FakeJob> Jobs { get; } = [];

    public List<ReimbursementImportFileResult> Outcomes { get; } = [];

    /// <summary>What <see cref="RecordFileOutcomeAsync"/> throws, for the failure cases.</summary>
    public Func<ReimbursementImportFileResult, Exception?>? OutcomeThrows { get; set; }

    public Task<int> CreateAsync(NewReimbursementImportJob job, CancellationToken cancellationToken)
    {
        Created.Add(job);

        var id = _nextId++;
        Jobs[id] = new FakeJob { State = ReimbursementImportJobState.Queued, HasArchive = true };

        return Task.FromResult(id);
    }

    public Task<IReadOnlyList<ReimbursementImportJobSummary>> ListRecentAsync(
        int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ReimbursementImportJobSummary>>([]);

    public Task<ReimbursementImportJobDetail?> FindAsync(
        int jobId, CancellationToken cancellationToken) =>
        Task.FromResult<ReimbursementImportJobDetail?>(null);

    public Task<ReimbursementImportJobClaimResult> ClaimAsync(
        int jobId, int maxAttempts, DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (!Jobs.TryGetValue(jobId, out var job) || ReimbursementImportJobStates.IsFinal(job.State))
        {
            return Task.FromResult(ReimbursementImportJobClaimResult.Refused(
                ReimbursementImportJobClaimState.NotClaimable));
        }

        if (job.Attempts >= maxAttempts)
        {
            return Task.FromResult(ReimbursementImportJobClaimResult.Refused(
                ReimbursementImportJobClaimState.AttemptsExhausted));
        }

        if (!job.HasArchive)
        {
            return Task.FromResult(ReimbursementImportJobClaimResult.Refused(
                ReimbursementImportJobClaimState.ArchiveMissing));
        }

        job.Attempts++;
        job.State = ReimbursementImportJobState.Processing;
        job.NextAttemptAtUtc = null;

        return Task.FromResult(ReimbursementImportJobClaimResult.Claimed(
            new ReimbursementImportJobClaim(
                jobId,
                "Reports_09-11-26.zip",
                job.Attempts,
                [1, 2, 3],
                Outcomes.Select(outcome => outcome.Index).ToList())));
    }

    public Task RecordFileOutcomeAsync(
        int jobId,
        ReimbursementImportFileResult outcome,
        DateTime recordedAtUtc,
        CancellationToken cancellationToken)
    {
        var failure = OutcomeThrows?.Invoke(outcome);
        if (failure is not null) throw failure;

        Outcomes.Add(outcome);

        return Task.CompletedTask;
    }

    public Task<ReimbursementImportFileCounts> CompleteAsync(
        int jobId,
        Func<ReimbursementImportFileCounts, ReimbursementImportJobState> stateFor,
        DateTime finishedAtUtc,
        CancellationToken cancellationToken)
    {
        var counts = ReimbursementImportJobCompletion.Count(
            Outcomes.Select(outcome => outcome.Outcome));

        var job = Jobs[jobId];
        job.State = stateFor(counts);
        job.HasArchive = false;
        job.FinishedAtUtc = finishedAtUtc;

        return Task.FromResult(counts);
    }

    public Task RequeueAsync(
        int jobId, DateTime nextAttemptAtUtc, string reason, CancellationToken cancellationToken)
    {
        var job = Jobs[jobId];
        job.State = ReimbursementImportJobState.Queued;
        job.NextAttemptAtUtc = nextAttemptAtUtc;
        job.LastError = reason;

        return Task.CompletedTask;
    }

    public Task FailAsync(
        int jobId, string reason, DateTime finishedAtUtc, CancellationToken cancellationToken)
    {
        var job = Jobs[jobId];
        job.State = ReimbursementImportJobState.Failed;
        job.LastError = reason;
        job.HasArchive = false;
        job.FinishedAtUtc = finishedAtUtc;

        return Task.CompletedTask;
    }

    /// <summary>One job as this fake holds it.</summary>
    internal sealed class FakeJob
    {
        public ReimbursementImportJobState State { get; set; }

        public int Attempts { get; set; }

        public DateTime? NextAttemptAtUtc { get; set; }

        public string? LastError { get; set; }

        public bool HasArchive { get; set; }

        public DateTime? FinishedAtUtc { get; set; }
    }
}

/// <summary>A per-file transaction double that records whether each file committed.</summary>
internal sealed class FakeImportFileTransactions : IReimbursementImportFileTransactions
{
    public List<bool> Committed { get; } = [];

    public Task<IReimbursementImportFileTransaction> BeginAsync(CancellationToken cancellationToken)
    {
        var transaction = new Transaction(this);
        Committed.Add(false);

        return Task.FromResult<IReimbursementImportFileTransaction>(transaction);
    }

    private sealed class Transaction : IReimbursementImportFileTransaction
    {
        private readonly FakeImportFileTransactions _owner;
        private readonly int _index;

        public Transaction(FakeImportFileTransactions owner)
        {
            _owner = owner;
            _index = owner.Committed.Count;
        }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            _owner.Committed[_index] = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>An import job store double for the reimbursement rows, with a failure switch.</summary>
internal sealed class FakeImportedReimbursementStore : IImportedReimbursementStore
{
    public HashSet<string> ExistingHashes { get; } = [];

    public List<string> Imported { get; } = [];

    /// <summary>Hashes whose import throws, for the "failure inside the transaction" cases.</summary>
    public HashSet<string> ImportThrowsFor { get; } = [];

    public Task<bool> HasFileWithContentHashAsync(
        string contentHash, CancellationToken cancellationToken) =>
        Task.FromResult(ExistingHashes.Contains(contentHash));

    public Task ImportAsync(
        PendingReimbursementXmlFile file, DateTime importedAtUtc, CancellationToken cancellationToken)
    {
        if (ImportThrowsFor.Contains(file.ContentHash))
            throw new InvalidOperationException("database is busy");

        Imported.Add(file.ContentHash);
        ExistingHashes.Add(file.ContentHash);

        return Task.CompletedTask;
    }
}
