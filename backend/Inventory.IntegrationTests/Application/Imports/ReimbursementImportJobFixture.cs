using Inventory.Application.Imports;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Imports;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace InventoryApi.Tests.Application.Imports;

/// <summary>
/// The world the uploaded reimbursement report import's database cases run in (issue #1149): two
/// businesses over <strong>relational SQLite</strong>, with the real adapters the composition root
/// wires.
///
/// <para>A real SQLite database rather than the EF InMemory provider, deliberately: the per-file
/// unit of work is a <c>BEGIN IMMEDIATE</c> transaction, the one-outcome-per-file rule is a unique
/// index, the content-hash dedupe is another, and several of these cases are about what a rolled-back
/// transaction leaves behind - none of which exists on the InMemory provider (AGENTS.md § Database
/// and migrations).</para>
/// </summary>
internal sealed class ReimbursementImportJobFixture : IAsyncDisposable
{
    /// <summary>The business whose archives are uploaded in most of the cases.</summary>
    public const int BusinessA = 1;

    /// <summary>A second business, for the isolation cases.</summary>
    public const int BusinessB = 2;

    /// <summary>The instant an upload is accepted in these cases.</summary>
    public static readonly DateTime UploadedAt = new(2026, 11, 9, 3, 0, 0, DateTimeKind.Utc);

    /// <summary>The instant the worker runs in these cases.</summary>
    public static readonly DateTime RunAt = new(2026, 11, 9, 3, 5, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;

    private ReimbursementImportJobFixture(SqliteConnection connection)
    {
        _connection = connection;
        Options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
    }

    public DbContextOptions<AppDbContext> Options { get; }

    /// <summary>The clock every adapter and use case in the fixture reads.</summary>
    public FakeClock Clock { get; } = new(RunAt);

    public static async Task<ReimbursementImportJobFixture> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var fixture = new ReimbursementImportJobFixture(connection);

        await using var setup = TestAppDbContext.Unrestricted(fixture.Options);
        await setup.Database.EnsureCreatedAsync();
        setup.Businesses.AddRange(
            Business(BusinessA, "Fictional Vending A"), Business(BusinessB, "Fictional Vending B"));
        await setup.SaveChangesAsync();

        return fixture;
    }

    /// <summary>A context acting as one business, exactly as a request or a worker scope would.</summary>
    public AppDbContext Context(int businessId = BusinessA) =>
        TestAppDbContext.For(Options, businessId);

    /// <summary>A context with no business resolved, as the worker's discovery scope has.</summary>
    public AppDbContext DeniedContext() => TestAppDbContext.Denied(Options);

    /// <summary>A context that sees every business, for arranging and reading back from outside.</summary>
    public AppDbContext Unrestricted() => TestAppDbContext.Unrestricted(Options);

    /// <summary>The real job store over <paramref name="db"/>.</summary>
    public static EfReimbursementImportJobStore Jobs(AppDbContext db) => new(db);

    /// <summary>The real archive reader, which holds nothing per request.</summary>
    public static ZipReimbursementArchiveReader Reader() =>
        new(NullLogger<ZipReimbursementArchiveReader>.Instance);

    /// <summary>
    /// The real per-file import pipeline over <paramref name="db"/>: the real reimbursement store and
    /// the real coverage store, exactly as the composition root wires them.
    /// </summary>
    public ImportReimbursementXmlFile ImportFile(AppDbContext db) =>
        new(new EfImportedReimbursementStore(db), Clock, new EfProviderDataCoverageStore(db, Clock));

    /// <summary>
    /// The real job processor over <paramref name="db"/>. <paramref name="importedStore"/> is how
    /// the "a failure inside a file's transaction" cases make one file's write throw after its
    /// reimbursement rows have been staged; left unset, it is the real EF store.
    /// </summary>
    public ProcessReimbursementImportJob Process(
        AppDbContext db, IImportedReimbursementStore? importedStore = null) =>
        new(
            Jobs(db),
            Reader(),
            new ImportReimbursementXmlFile(
                importedStore ?? new EfImportedReimbursementStore(db),
                Clock,
                new EfProviderDataCoverageStore(db, Clock)),
            new EfReimbursementImportFileTransactions(db),
            Clock);

    /// <summary>The real cross-business discovery read over <paramref name="db"/>.</summary>
    public EfReimbursementImportJobQueue Queue(AppDbContext db) => new(db, Clock);

    /// <summary>
    /// Stores one uploaded archive as a queued job of <paramref name="businessId"/> and answers its
    /// key, through the real store and the real ownership stamp.
    /// </summary>
    public async Task<int> QueueJobAsync(
        byte[] archive, int businessId = BusinessA, DateTime? uploadedAtUtc = null)
    {
        await using var db = Context(businessId);

        return await Jobs(db).CreateAsync(
            new NewReimbursementImportJob(
                "Reports_09-11-26.zip", Actor(), uploadedAtUtc ?? UploadedAt, archive),
            CancellationToken.None);
    }

    /// <summary>One job row as the database holds it, read from outside the boundary.</summary>
    public async Task<ReimbursementImportJob> JobRowAsync(int jobId)
    {
        await using var db = Unrestricted();

        return await db.ReimbursementImportJobs
            .AsNoTracking()
            .SingleAsync(job => job.Id == jobId);
    }

    /// <summary>One job's recorded file outcomes, in the archive's own order.</summary>
    public async Task<List<ReimbursementImportJobFile>> FileRowsAsync(int jobId)
    {
        await using var db = Unrestricted();

        return await db.ReimbursementImportJobFiles
            .AsNoTracking()
            .Where(file => file.ReimbursementImportJobId == jobId)
            .OrderBy(file => file.EntryIndex)
            .ToListAsync();
    }

    /// <summary>A made-up uploader identity, as a validated token would supply one.</summary>
    public static ActorIdentity Actor()
    {
        ActorIdentity.TryCreate(
            "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222",
            out var identity);

        return identity!;
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private static Inventory.Infrastructure.Models.Business Business(int id, string name) => new()
    {
        Id = id,
        Name = name,
        Status = BusinessStatus.Active,
        SalesProvider = ExternalProvider.Nayax,
        CreatedAtUtc = UploadedAt.AddYears(-1),
    };
}

/// <summary>
/// The real reimbursement store with one file's write made to fail <em>after</em> the rows are
/// staged, so a test can prove that a rolled-back transaction leaves neither the rows nor the
/// outcome behind (issue #1149).
/// </summary>
internal sealed class FailingImportedReimbursementStore : IImportedReimbursementStore
{
    private readonly EfImportedReimbursementStore _inner;
    private readonly string _failForHash;

    public FailingImportedReimbursementStore(AppDbContext db, string failForHash)
    {
        _inner = new EfImportedReimbursementStore(db);
        _failForHash = failForHash;
    }

    public Task<bool> HasFileWithContentHashAsync(
        string contentHash, CancellationToken cancellationToken) =>
        _inner.HasFileWithContentHashAsync(contentHash, cancellationToken);

    public async Task ImportAsync(
        PendingReimbursementXmlFile file, DateTime importedAtUtc, CancellationToken cancellationToken)
    {
        await _inner.ImportAsync(file, importedAtUtc, cancellationToken);

        if (file.ContentHash == _failForHash)
        {
            // After the real write, inside the file's own transaction: this is the shape of a
            // database failure that happens once some of the file's statements have run.
            throw new InvalidOperationException("the database became unavailable mid-file");
        }
    }
}
