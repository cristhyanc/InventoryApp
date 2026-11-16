using System.Data.Common;
using Inventory.Application.Commissions;
using Inventory.Domain.Exceptions;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Gst;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using Agreement = Inventory.Domain.FinancialConfiguration.CommissionAgreement;
using AgreementEntity = Inventory.Infrastructure.Models.SiteCommissionAgreement;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The two commission-agreement writes of issue #1254 under <strong>real</strong> concurrency: the
/// acceptance criterion "the overlap check and the end-date update run in one write-locked
/// transaction", raced rather than staged.
///
/// <para><c>EndSiteCommissionAgreementTests</c> (Inventory.UnitTests)
/// pins the port call order against a hand-written store - that the transaction opens before the
/// authoritative read and commits after the write - and it stays, because it is the test that cannot
/// pass by accident. It cannot, however, show that a <em>second</em> genuine writer is serialised
/// behind the first: a single caller invoking four ports in the right order proves nothing about a
/// lock. These tests close that gap, and they close it for
/// <see cref="SaveSiteCommissionAgreement"/> too, which before this issue ran its overlap check and
/// its insert with no transaction around them at all.</para>
///
/// <para>Everything runs against a real SQLite <strong>file</strong> with one
/// <see cref="AppDbContext"/> - and therefore one connection - per writer, exactly as
/// <see cref="InventoryCountApplyConcurrencyTests"/> and <see cref="SupplierPresetConcurrencyTests"/>
/// do and for the same reason: a shared in-memory database is one connection, so two "concurrent"
/// writers over it contend for the connection rather than for the write lock, and the EF InMemory
/// provider has no locking at all.</para>
///
/// <para><strong>The window is opened deliberately rather than hoped for.</strong> Three of these
/// tests hold the write lock from an uncommitted writer and release it at a chosen instant, so what
/// the waiting writer reads is decided rather than timed; the fourth drives a competing writer from a
/// <see cref="DbCommandInterceptor"/> at the exact instant the first writer's overlap check has
/// executed - the gap a second insert used to be decided in - on its own, deliberately impatient
/// connection, so "it cannot even begin" is answered in a second rather than after SQLite's default
/// thirty. The last is the production shape of the same race: both writers released together.</para>
/// </summary>
public sealed class SiteCommissionAgreementConcurrencyTests : IDisposable
{
    private const int BusinessA = 1;

    /// <summary>The site whose 10% agreement is seeded open-ended, so it can be ended.</summary>
    private const long SiteId = 42;

    /// <summary>A site with no agreement at all, so two competing creates both legitimately find none.</summary>
    private const long UncoveredSiteId = 43;

    private const string Tid = "99999999-9999-9999-9999-999999999999";
    private const string Oid = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

    /// <summary>
    /// <see cref="SaveSiteCommissionAgreement"/>'s own refusal, which the API maps to <c>409</c>:
    /// the answer a losing create must get, rather than a provider lock error or a second row.
    /// </summary>
    private const string OverlapMessage = "The agreement overlaps an existing agreement for this site.";

    private static readonly DateTime Start = new(2026, 1, 1);
    private static readonly DateTime JuneEnd = new(2026, 6, 30);
    private static readonly DateTime MarchEnd = new(2026, 3, 31);
    private static readonly DateTime JulyStart = new(2026, 7, 1);
    private static readonly DateTime Now = new(2026, 11, 16, 3, 30, 0, DateTimeKind.Utc);

    /// <summary>
    /// How long a writer that already holds the write lock stays open before committing. It is a
    /// window for the second writer to arrive and start waiting for that lock, not a synchronisation
    /// point: whenever it arrives, the lock is what orders the two, and it reads the committed state
    /// rather than one that is about to change.
    /// </summary>
    private static readonly TimeSpan LockWaitWindow = TimeSpan.FromSeconds(1);

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"site-commission-agreements-{Guid.NewGuid():N}.db");

    private readonly string _connectionString;
    private readonly string _impatientConnectionString;

    public SiteCommissionAgreementConcurrencyTests()
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString();
        _impatientConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            // One second, not the ADO.NET "0 means wait forever" and not SQLite's default thirty.
            DefaultTimeout = 1,
        }.ToString();

        // The real migration history, so the table is the one production gets.
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

        // The issue's own example: an open-ended 10% agreement from 1 January 2026, which blocks a
        // 12% agreement from 1 July 2026 until it is ended.
        db.SiteCommissionAgreements.Add(new AgreementEntity
        {
            BusinessId = BusinessA,
            SiteId = SiteId,
            EffectiveFrom = Start,
            EffectiveTo = null,
            CommissionRate = .10m,
            Frequency = CommissionFrequency.Monthly,
            Basis = CommissionBasis.GrossSales,
            PaymentDueDaysAfterPeriodEnd = 14,
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

    /// <summary>
    /// The issue's own example, raced: a create re-checks overlap <em>after</em> a concurrent end,
    /// because its check runs inside the write lock that end was holding. The 12% agreement from
    /// 1 July arrives while "end the 10% agreement on 30 June" is written but not committed, so it
    /// waits, reads the agreement as the end left it, and is allowed.
    ///
    /// <para>Without the lock around the check the create would have read the still-open-ended
    /// agreement that the end was about to replace and refused itself with a <c>409</c> describing a
    /// state that no longer existed - a stale refusal of a perfectly good rate change, which is
    /// exactly the interleaving the acceptance criterion rules out.</para>
    /// </summary>
    [Fact]
    public async Task A_create_rechecks_overlap_against_an_end_that_committed_while_it_waited()
    {
        await using var ending = await BeginUncommittedEndAsync(JuneEnd);

        var create = Task.Run(() => AddAsync(SiteId, JulyStart, null, .12m));

        await Task.Delay(LockWaitWindow);
        await ending.CommitAsync(CancellationToken.None);

        var answer = await create;

        Assert.Null(answer.Failure);
        Assert.Equal(JulyStart, answer.Agreement!.EffectiveFrom);
        Assert.Equal(.12m, answer.Agreement.CommissionRate);

        // Two agreements that meet without overlapping: 1 January - 30 June at 10%, then 1 July on
        // at 12%. Neither the end nor the create was lost, and neither covers a date the other does.
        var stored = ReadAgreements(SiteId);
        Assert.Equal(2, stored.Count);
        Assert.Equal(JuneEnd, stored[0].EffectiveTo);
        Assert.Equal(.10m, stored[0].CommissionRate);
        Assert.Equal(JulyStart, stored[1].EffectiveFrom);
        Assert.Null(stored[1].EffectiveTo);
    }

    /// <summary>
    /// The same guarantee on the end path, and the one the mocked call-order test cannot give: the
    /// agreement an end date is measured against is read inside the lock, so an end that waited is
    /// decided on the end date the other end committed rather than on the one it saw before waiting.
    ///
    /// <para>Ending to 31 March commits first; ending to 30 June then re-reads it and is refused,
    /// because an agreement may be brought forward and never pushed out. Without the in-lock read the
    /// second end would have seen the open-ended agreement, passed the policy and written 30 June on
    /// top of a committed 31 March - silently <em>extending</em> an agreement over three months of
    /// sales, which is the one thing <see cref="SiteCommissionAgreementEndPolicy"/> exists to
    /// forbid.</para>
    /// </summary>
    [Fact]
    public async Task An_end_that_waited_is_decided_on_the_end_date_the_other_end_committed()
    {
        await using var ending = await BeginUncommittedEndAsync(MarchEnd);

        var audit = new RecordingAudit();
        var second = Task.Run(() => EndAsync(AgreementId(), JuneEnd, audit));

        await Task.Delay(LockWaitWindow);
        await ending.CommitAsync(CancellationToken.None);

        var answer = await second;

        Assert.Null(answer.Agreement);
        Assert.Equal(
            SiteCommissionAgreementEndPolicy.LaterThanExistingEndMessage,
            Assert.IsType<DomainValidationException>(answer.Failure).Message);

        // The committed end date stands, and the refused end wrote nothing and audited nothing.
        var stored = Assert.Single(ReadAgreements(SiteId));
        Assert.Equal(MarchEnd, stored.EffectiveTo);
        Assert.Empty(audit.Entries);
    }

    /// <summary>
    /// The create's own check-then-write, closed against a second create. A competing overlapping
    /// create is driven at the instant the first one's overlap check has executed - the gap both
    /// inserts used to be decided in - and cannot even begin, because the first create took the write
    /// lock before that check. The first commits, and the site holds one agreement.
    ///
    /// <para>Before this issue there was no transaction here at all: both creates read "no agreement
    /// covers this range", because neither one's row existed yet, and both inserted. There is no
    /// unique index to catch that - an agreement is a date range, not a name - so the site was left
    /// with two overlapping agreements, which the repository treats as a configuration problem that
    /// makes every affected report provisional.</para>
    /// </summary>
    [Fact]
    public async Task A_competing_create_cannot_begin_between_a_creates_overlap_check_and_its_insert()
    {
        var competing = new CompetesWhenTheOverlapCheckHasRun(
            () => AddAsync(UncoveredSiteId, Start, null, .15m, ImpatientOptions()));

        var answer = await AddAsync(
            UncoveredSiteId, JulyStart, null, .12m, OptionsWith(competing));

        Assert.True(competing.Ran, "The competing create was never attempted, so nothing was proved.");
        Assert.Null(answer.Failure);

        // It could not take the write lock, so it never reached its own overlap check or its insert.
        Assert.IsType<SqliteException>(competing.Failure);
        var stored = Assert.Single(ReadAgreements(UncoveredSiteId));
        Assert.Equal(answer.Agreement!.Id, stored.Id);
        Assert.Equal(.12m, stored.CommissionRate);
    }

    /// <summary>
    /// The patient half of the same guarantee, and the one the acceptance criterion is written in
    /// terms of: the create that <em>waits</em> for the write lock re-checks overlap against the
    /// state the winner committed, and is refused with the same sentence a sequential overlap gets.
    ///
    /// <para>Without the lock around the check the loser would have read the committed state the
    /// winner's uncommitted insert had not reached yet - "no agreement covers this range" - inserted
    /// anyway once the winner released the lock, and left the site with two overlapping agreements
    /// and no refusal at all.</para>
    /// </summary>
    [Fact]
    public async Task A_create_that_waited_is_refused_against_the_agreement_the_other_create_committed()
    {
        await using var winner = await BeginUncommittedCreateAsync(UncoveredSiteId, Start, .10m);

        var loser = Task.Run(() => AddAsync(UncoveredSiteId, JulyStart, null, .12m));

        await Task.Delay(LockWaitWindow);
        await winner.CommitAsync(CancellationToken.None);

        var answer = await loser;

        Assert.Null(answer.Agreement);
        Assert.Equal(
            OverlapMessage,
            Assert.IsType<DomainConflictException>(answer.Failure).Message);

        var stored = Assert.Single(ReadAgreements(UncoveredSiteId));
        Assert.Equal(Start, stored.EffectiveFrom);
        Assert.Equal(.10m, stored.CommissionRate);
    }

    /// <summary>
    /// The production shape of the same race: two overlapping agreements for one site submitted at
    /// the same instant. Exactly one is created and the other is refused with the sentence a
    /// sequential overlap gets - the <c>409</c> the surface answers with, because
    /// <see cref="DomainConflictException"/> is mapped to that status by the API's exception handler -
    /// and the site is left holding one agreement rather than two that overlap or a provider lock
    /// error escaping as a <c>500</c>.
    /// </summary>
    [Fact]
    public async Task Two_simultaneous_overlapping_creates_leave_one_agreement_and_one_refusal()
    {
        var answers = await RaceAsync(
            () => AddAsync(UncoveredSiteId, Start, null, .10m),
            () => AddAsync(UncoveredSiteId, JulyStart, new DateTime(2026, 12, 31), .12m));

        var created = Assert.Single(answers.Where(answer => answer.Agreement is not null)).Agreement!;
        var refused = Assert.Single(answers.Where(answer => answer.Failure is not null)).Failure!;

        Assert.Equal(OverlapMessage, Assert.IsType<DomainConflictException>(refused).Message);

        var stored = Assert.Single(ReadAgreements(UncoveredSiteId));
        Assert.Equal(created.Id, stored.Id);
        Assert.Equal(created.EffectiveFrom, stored.EffectiveFrom);
    }

    /// <summary>What one write answered: the agreement it produced, or the exception it was refused with.</summary>
    private sealed record Answer(Agreement? Agreement, Exception? Failure);

    /// <summary>Releases both writers at the same instant and returns what each of them answered.</summary>
    private static async Task<Answer[]> RaceAsync(params Func<Task<Answer>>[] writers)
    {
        using var barrier = new Barrier(writers.Length);

        return await Task.WhenAll(writers.Select(write => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await write();
        })));
    }

    private Task<Answer> AddAsync(
        long siteId, DateTime effectiveFrom, DateTime? effectiveTo, decimal rate) =>
        AddAsync(siteId, effectiveFrom, effectiveTo, rate, Options());

    /// <summary>One add request: its own context, and therefore its own connection.</summary>
    private static async Task<Answer> AddAsync(
        long siteId,
        DateTime effectiveFrom,
        DateTime? effectiveTo,
        decimal rate,
        DbContextOptions<AppDbContext> options)
    {
        await using var db = TestAppDbContext.For(options, BusinessA);
        try
        {
            var added = await new SaveSiteCommissionAgreement(new EfSiteCommissionStore(db)).Handle(
                new SaveSiteCommissionAgreementInput(
                    siteId,
                    effectiveFrom,
                    effectiveTo,
                    rate,
                    CommissionFrequency.Monthly,
                    CommissionBasis.GrossSales,
                    14),
                CancellationToken.None);
            return new Answer(added, null);
        }
        catch (Exception failure)
        {
            return new Answer(null, failure);
        }
    }

    /// <summary>One end request, through the real use case with its real clock, actor and audit port.</summary>
    private async Task<Answer> EndAsync(int agreementId, DateTime endDate, ISiteCommissionAgreementAudit audit)
    {
        await using var db = TestAppDbContext.For(Options(), BusinessA);
        try
        {
            var ended = await new EndSiteCommissionAgreement(
                    new EfSiteCommissionStore(db),
                    new FakeCurrentBusinessProvider(BusinessA),
                    FakeAuthenticatedActorAccessor.Identified(Actor()),
                    audit,
                    new FakeClock(Now))
                .Handle(new EndSiteCommissionAgreementInput(agreementId, endDate), CancellationToken.None);
            return new Answer(ended, null);
        }
        catch (Exception failure)
        {
            return new Answer(null, failure);
        }
    }

    /// <summary>
    /// An end of the seeded agreement that has taken SQLite's write lock and written its new end
    /// date, and is not committed yet - another request mid-flight. It is the use case's own two
    /// steps, through the same store and the same <c>BEGIN IMMEDIATE</c> transaction, with the commit
    /// left to the test.
    /// </summary>
    private async Task<UncommittedWrite> BeginUncommittedEndAsync(DateTime endDate)
    {
        var db = TestAppDbContext.For(Options(), BusinessA);
        var store = new EfSiteCommissionStore(db);
        var transaction = await store.BeginWriteTransactionAsync(CancellationToken.None);
        await store.SetAgreementEndDateAsync(AgreementId(), endDate, Now, CancellationToken.None);
        return new UncommittedWrite(db, transaction);
    }

    /// <summary>
    /// An open-ended agreement that has taken SQLite's write lock and been inserted, and is not
    /// committed yet - another create mid-flight, between its own overlap check and its commit.
    /// </summary>
    private async Task<UncommittedWrite> BeginUncommittedCreateAsync(
        long siteId, DateTime effectiveFrom, decimal rate)
    {
        var db = TestAppDbContext.For(Options(), BusinessA);
        var store = new EfSiteCommissionStore(db);
        var transaction = await store.BeginWriteTransactionAsync(CancellationToken.None);
        await store.AddAgreementAsync(
            new Agreement(
                0, siteId, effectiveFrom, null, rate, CommissionFrequency.Monthly,
                CommissionBasis.GrossSales, 14, default, default),
            CancellationToken.None);
        return new UncommittedWrite(db, transaction);
    }

    private sealed class UncommittedWrite(AppDbContext db, ISiteCommissionWriteTransaction transaction)
        : IAsyncDisposable
    {
        public Task CommitAsync(CancellationToken cancellationToken) =>
            transaction.CommitAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await db.DisposeAsync();
        }
    }

    private int AgreementId()
    {
        using var db = TestAppDbContext.Unrestricted(Options());
        return db.SiteCommissionAgreements.AsNoTracking().Single(row => row.SiteId == SiteId).Id;
    }

    private List<AgreementEntity> ReadAgreements(long siteId)
    {
        using var db = TestAppDbContext.Unrestricted(Options());
        return db.SiteCommissionAgreements.AsNoTracking()
            .Where(row => row.SiteId == siteId)
            .OrderBy(row => row.EffectiveFrom)
            .ToList();
    }

    private static ActorIdentity Actor()
    {
        Assert.True(ActorIdentity.TryCreate(Tid, Oid, out var actor));
        return actor!;
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

    /// <summary>The audit port, recording what a committed end emitted - and what a refused one did not.</summary>
    private sealed class RecordingAudit : ISiteCommissionAgreementAudit
    {
        private readonly List<SiteCommissionAgreementEndedAuditEntry> _entries = [];

        public IReadOnlyList<SiteCommissionAgreementEndedAuditEntry> Entries
        {
            get
            {
                lock (_entries)
                {
                    return _entries.ToList();
                }
            }
        }

        public void Record(SiteCommissionAgreementEndedAuditEntry entry)
        {
            lock (_entries)
            {
                _entries.Add(entry);
            }
        }
    }

    /// <summary>
    /// Runs a competing write once the create's overlap check has executed - the answer its insert is
    /// allowed by. That is the instant the unserialised implementation was exposed at: with no lock
    /// held around that check, a second create read the same "nothing covers this range" and inserted
    /// its own overlapping agreement.
    /// </summary>
    private sealed class CompetesWhenTheOverlapCheckHasRun(Func<Task<Answer>> competing) : DbCommandInterceptor
    {
        private bool _attempted;

        /// <summary>Whether the window was reached at all; a test that never reaches it proves nothing.</summary>
        public bool Ran { get; private set; }

        public Exception? Failure { get; private set; }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);
            Compete(command.CommandText);
            return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }

        private void Compete(string? commandText)
        {
            if (_attempted ||
                commandText?.Contains(@"""SiteCommissionAgreements""", StringComparison.Ordinal) != true)
            {
                return;
            }

            _attempted = true;
            Ran = true;

            // Blocking is deliberate: the competing request has to be resolved inside this window,
            // and it runs on its own impatient connection, so nothing about this create's own
            // connection is waiting on it. `Task.Run` is what keeps blocking safe - it takes the
            // competing write off the test runner's limited synchronization context, which this
            // thread is sitting on while it waits.
            Failure = Task.Run(competing).GetAwaiter().GetResult().Failure;
        }
    }
}
