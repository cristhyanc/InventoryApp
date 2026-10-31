using Inventory.Application.PlatformAdmin;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Suppliers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The platform supplier presets of issue #848 under <strong>real</strong> concurrency: the
/// acceptance criterion "two concurrent creates with the same name yield one row and one
/// <c>409</c>, never a <c>500</c>", raced rather than staged.
///
/// <para><see cref="EfSupplierPresetStoreTests"/> proves the same rule deterministically, by making
/// the real adapter's pre-check blind - which is what the losing request of a race sees - and that
/// test stays, because it is the one that cannot pass by accident. This one closes the gap it leaves:
/// a blind pre-check is a stand-in for two requests, and a stand-in cannot prove that two genuine
/// writers on two connections end with one row rather than with a deadlock, a
/// <c>SQLITE_BUSY</c> escaping as a <c>500</c>, or two rows.</para>
///
/// <para>It runs against a real SQLite <strong>file</strong> with one service scope per request,
/// each with its own <see cref="AppDbContext"/> and therefore its own connection, exactly as
/// <see cref="MemberManagementConcurrencyTests"/> does and for the same reason: a shared in-memory
/// database is one connection, so two "concurrent" requests over it contend for the connection
/// rather than for the index. Every context carries a <strong>denied</strong> business scope, which
/// is what a platform-admin request carries.</para>
///
/// <para><strong>The barrier sits after the pre-check</strong>, which is the whole point: both
/// requests read "no such name" - the answer neither of them is wrong to get, because the other's
/// row does not exist yet - and are then released into their inserts together. What settles it is
/// the unique index on the stored normalised name, and what turns the loser's database error into
/// the same conflict a sequential duplicate gets is <see cref="CreateSupplierPreset"/> catching
/// exactly that violation. The conflict is the <c>409</c> the surface answers with:
/// <see cref="DomainConflictException"/> is mapped to that status by the API's exception handler,
/// which <c>SuperAdminSupplierPresetsApiTests</c> asserts over HTTP.</para>
/// </summary>
public class SupplierPresetConcurrencyTests : IDisposable
{
    private const string Tid = "99999999-9999-9999-9999-999999999999";
    private const string Oid = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

    /// <summary>
    /// A name no shipped preset holds, so both pre-checks legitimately find nothing. It is
    /// deliberately not one of the nine the migrations insert - "Aldi" stopped being available the
    /// day issue #931 seeded ALDI, and a name that collides would make both requests lose for the
    /// wrong reason.
    /// </summary>
    private const string Name = "Harris Farm Markets";

    /// <summary>
    /// Long enough that a loaded CI runner does not fail this test for being slow, and short enough
    /// that a genuine deadlock is reported as one rather than hanging until the test host's own
    /// 90-second limit fires.
    /// </summary>
    private static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(30);

    private static readonly DateTime CreatedAt = new(2026, 10, 23, 2, 0, 0, DateTimeKind.Utc);

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"inventory-supplier-presets-{Guid.NewGuid():N}.db");

    private readonly string _connectionString;

    public SupplierPresetConcurrencyTests()
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString();

        // The real migration history, so the table, its unique index and the nine shipped rows are
        // the ones production gets.
        using var db = TestAppDbContext.Unrestricted(Options());
        db.Database.Migrate();
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
    /// Two requests creating the same preset at the same instant - typed differently, because the
    /// rule ignores case and padding. Exactly one is created, the other is refused with the Domain's
    /// own sentence, the table holds one row for that name, and the audit trail holds one event: the
    /// refused request wrote nothing, so it recorded nothing.
    /// </summary>
    [Fact]
    public async Task Two_concurrent_creates_of_one_name_leave_one_preset_one_conflict_and_one_audit_event()
    {
        var audit = new RecordingAudit();
        using var barrier = new Barrier(participantCount: 2);
        await using var host = BuildHost(audit, barrier);

        var attempts = await Task.WhenAll(
            CreateAsync(host, $"  {Name}  "),
            CreateAsync(host, Name.ToLowerInvariant()));

        var created = Assert.Single(attempts.Where(attempt => attempt.Preset is not null)).Preset!;
        var refused = Assert.Single(attempts.Where(attempt => attempt.Failure is not null)).Failure!;

        Assert.Equal(
            SupplierPresetPolicy.DuplicateNameMessage,
            Assert.IsType<DomainConflictException>(refused).Message);

        var presets = ReadPresets();
        var stored = Assert.Single(presets.Where(
            preset => preset.NormalisedName == SupplierPresetPolicy.NormalisedName(Name)));
        Assert.Equal(created.Id, stored.Id);
        Assert.Equal(created.Name, stored.Name);
        Assert.True(stored.IsEnabled);
        // The nine shipped rows and exactly one new one: the loser left nothing behind, not even a
        // row the index happened to accept under a different normalised value.
        Assert.Equal(10, presets.Count);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(created.Id, entry.PresetId);
        Assert.Equal(SupplierPresetChange.Created, entry.Change);
        Assert.True(entry.IsEnabled);
        // The supplier-master fields the winning create submitted reached the trail too (issue #931).
        Assert.True(entry.ServesNationwide);
        Assert.Equal(string.Empty, entry.ServedStates);
        Assert.True(entry.PricesIncludeGst);
        Assert.Null(entry.PriceConnector);
        Assert.Null(entry.PriceConnectorKey);
        Assert.Equal(CreatedAt, entry.OccurredAtUtc);
        // The actor as ActorIdentity normalises it, which is how every other audit records one.
        Assert.Equal(Actor().DirectoryTenantId, entry.ActorDirectoryTenantId);
        Assert.Equal(Actor().ObjectId, entry.ActorObjectId);
    }

    /// <summary>
    /// One request: its own service scope, its own <see cref="AppDbContext"/> and its own connection,
    /// on its own thread, because the barrier that releases the two inserts together blocks the
    /// thread it is signalled on.
    /// </summary>
    private static Task<Attempt> CreateAsync(IServiceProvider host, string name) =>
        Task.Run(async () =>
        {
            await using var scope = host.CreateAsyncScope();
            var create = scope.ServiceProvider.GetRequiredService<CreateSupplierPreset>();

            try
            {
                var submitted = new SupplierPresetSubmission(
                    name,
                    null,
                    ServesNationwide: true,
                    ServedStates: null,
                    PricesIncludeGst: true,
                    PriceConnector: null,
                    PriceConnectorKey: null);

                return new Attempt(await create.Handle(submitted, CancellationToken.None), null);
            }
            catch (Exception exception)
            {
                return new Attempt(null, exception);
            }
        });

    /// <summary>
    /// The composition this feature actually has, with two substitutions a test must make - the
    /// clock and the actor - and one it exists to make: the pre-check's barrier.
    /// </summary>
    private ServiceProvider BuildHost(ISupplierPresetAudit audit, Barrier barrier)
    {
        var services = new ServiceCollection();

        // Scoped and denied, stated rather than defaulted (see TestAppDbContext): a platform-admin
        // request resolves no business at all, so the tenant query filters and the ownership
        // enforcer stay in force over everything except this platform table.
        services.AddScoped(_ => TestAppDbContext.Denied(Options()));
        services.AddScoped<ISupplierPresetStore>(provider => new BarrierAfterPreCheckStore(
            new EfSupplierPresetStore(provider.GetRequiredService<AppDbContext>()),
            barrier));
        services.AddScoped<CreateSupplierPreset>();
        services.AddSingleton(audit);
        services.AddSingleton<IAuthenticatedActorAccessor>(
            FakeAuthenticatedActorAccessor.Identified(Actor()));
        services.AddSingleton<IClock>(new FakeClock(CreatedAt));

        return services.BuildServiceProvider();
    }

    private DbContextOptions<AppDbContext> Options() =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connectionString).Options;

    private List<SupplierPreset> ReadPresets()
    {
        using var db = TestAppDbContext.Unrestricted(Options());
        return db.SupplierPresets.AsNoTracking().OrderBy(preset => preset.Id).ToList();
    }

    private static ActorIdentity Actor()
    {
        Assert.True(ActorIdentity.TryCreate(Tid, Oid, out var actor));
        return actor!;
    }

    private sealed record Attempt(SupplierPresetRecord? Preset, Exception? Failure);

    /// <summary>
    /// The real adapter, with the race opened where no pre-check can close it: the pre-check runs for
    /// real, and the two requests are released into their inserts only once both have been answered.
    /// Everything else is delegated, so the insert, the unique index and the exception translation
    /// under test are all the real ones.
    /// </summary>
    private sealed class BarrierAfterPreCheckStore : ISupplierPresetStore
    {
        private readonly ISupplierPresetStore _inner;
        private readonly Barrier _barrier;

        public BarrierAfterPreCheckStore(ISupplierPresetStore inner, Barrier barrier)
        {
            _inner = inner;
            _barrier = barrier;
        }

        public async Task<bool> ExistsWithNormalisedNameAsync(
            string normalisedName,
            int? excludingId,
            CancellationToken cancellationToken)
        {
            var exists = await _inner
                .ExistsWithNormalisedNameAsync(normalisedName, excludingId, cancellationToken)
                .ConfigureAwait(false);

            // If either request could already see the other's row, the race under test never
            // happened and a passing test would prove nothing.
            Assert.False(
                exists,
                "Both pre-checks must answer 'no such name' for this to be the race under test.");
            Assert.True(
                _barrier.SignalAndWait(BarrierTimeout),
                "The other request never reached the pre-check, so the inserts were not concurrent.");

            return exists;
        }

        public Task<IReadOnlyList<SupplierPresetRecord>> ListOrderedByNameAsync(
            CancellationToken cancellationToken) => _inner.ListOrderedByNameAsync(cancellationToken);

        public Task<SupplierPresetRecord> AddAsync(
            NewSupplierPreset preset,
            CancellationToken cancellationToken) => _inner.AddAsync(preset, cancellationToken);

        public Task<SupplierPresetRecord?> UpdateAsync(
            int id,
            SupplierPresetEdit edit,
            CancellationToken cancellationToken) => _inner.UpdateAsync(id, edit, cancellationToken);

        public bool IsDuplicateName(Exception exception) => _inner.IsDuplicateName(exception);
    }

    /// <summary>
    /// Records what was audited, from two threads, so "exactly one event" is a claim about the whole
    /// race rather than about whichever request happened to finish first.
    /// </summary>
    private sealed class RecordingAudit : ISupplierPresetAudit
    {
        private readonly List<SupplierPresetAuditEntry> _entries = [];

        public IReadOnlyList<SupplierPresetAuditEntry> Entries
        {
            get
            {
                lock (_entries)
                {
                    return _entries.ToArray();
                }
            }
        }

        public void Record(SupplierPresetAuditEntry entry)
        {
            lock (_entries)
            {
                _entries.Add(entry);
            }
        }
    }
}
