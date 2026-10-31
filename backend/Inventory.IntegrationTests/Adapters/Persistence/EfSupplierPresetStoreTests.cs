using Inventory.Application.PlatformAdmin;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Suppliers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The EF adapter over the platform supplier presets (issue #848), on a relational SQLite database,
/// because what it has to get right is relational: a unique index over a stored normalised name, and
/// the translation of that index's refusal into the one conflict the caller is told about.
///
/// <para>The race these tests exist for is the one no pre-check can win. Two creates of the same
/// name can both pass "does this name exist?", because the row the other one is about to insert does
/// not exist yet. What settles it is the index - and what turns the loser's database error into a
/// <c>409</c> rather than a <c>500</c> is the use case catching exactly that violation. The race is
/// staged deterministically here rather than by racing two requests, because a test that depends on
/// two threads interleaving at the right instant proves nothing on the run where they do not.</para>
/// </summary>
public class EfSupplierPresetStoreTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 10, 23, 2, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A name no shipped preset holds, so a create of it is a create and not a duplicate. It is
    /// deliberately not one of the nine the migrations insert - "Aldi" stopped being available the
    /// day issue #931 seeded ALDI, which is exactly the kind of collision a fixed name hides.
    /// </summary>
    private const string UnusedName = "Harris Farm Markets";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        await using var db = Context();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    /// <summary>
    /// A context with a <em>denied</em> business scope - exactly what a platform-admin request
    /// carries. The adapter has to work under it, which is the whole reason this table is not
    /// tenant-owned: a central filter keyed to a resolved business would match none of its rows and
    /// the ownership stamp would have no business to stamp.
    /// </summary>
    private AppDbContext Context() =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options,
            new BusinessScope());

    [Fact]
    public async Task The_list_is_readable_and_writable_under_a_denied_business_scope()
    {
        await using var db = Context();
        var store = new EfSupplierPresetStore(db);

        var created = await store.AddAsync(NewPreset(UnusedName), CancellationToken.None);

        Assert.True(created.Id > 0);
        Assert.True(created.IsEnabled);
        Assert.Contains(
            await store.ListOrderedByNameAsync(CancellationToken.None),
            preset => preset.Id == created.Id);
    }

    /// <summary>
    /// The supplier-master fields of issue #931 survive the round trip, and the served states come
    /// back as the list the resource publishes rather than as the string the column holds: the
    /// adapter is the one place that translation happens, in both directions.
    /// </summary>
    [Fact]
    public async Task The_supplier_master_fields_are_stored_and_read_back()
    {
        await using var db = Context();
        var store = new EfSupplierPresetStore(db);

        var created = await store.AddAsync(
            NewPreset(
                UnusedName,
                servesNationwide: false,
                servedStates: "NSW,ACT",
                pricesIncludeGst: false,
                connector: SupplierPriceConnector.Varnmill,
                connectorKey: "harris"),
            CancellationToken.None);

        Assert.False(created.ServesNationwide);
        Assert.Equal(["NSW", "ACT"], created.ServedStates);
        Assert.False(created.PricesIncludeGst);
        Assert.Equal(SupplierPriceConnector.Varnmill, created.PriceConnector);
        Assert.Equal("harris", created.PriceConnectorKey);

        // Read back through the list's own projection, which is a different code path from the write's
        // answer and must not disagree with it. The fields are compared one by one rather than as
        // whole records, because a record holding a list compares that list by reference.
        var listed = (await store.ListOrderedByNameAsync(CancellationToken.None))
            .Single(preset => preset.Id == created.Id);
        Assert.Equal(created.ServesNationwide, listed.ServesNationwide);
        Assert.Equal(created.ServedStates, listed.ServedStates);
        Assert.Equal(created.PricesIncludeGst, listed.PricesIncludeGst);
        Assert.Equal(created.PriceConnector, listed.PriceConnector);
        Assert.Equal(created.PriceConnectorKey, listed.PriceConnectorKey);

        var edited = await store.UpdateAsync(
            created.Id,
            new SupplierPresetEdit(
                UnusedName,
                SupplierPresetPolicy.NormalisedName(UnusedName),
                null,
                IsEnabled: true,
                ServesNationwide: true,
                ServedStates: string.Empty,
                PricesIncludeGst: true,
                PriceConnector: null,
                PriceConnectorKey: null,
                Now),
            CancellationToken.None);

        Assert.NotNull(edited);
        Assert.True(edited.ServesNationwide);
        Assert.Empty(edited.ServedStates);
        Assert.True(edited.PricesIncludeGst);
        Assert.Null(edited.PriceConnector);
        Assert.Null(edited.PriceConnectorKey);
    }

    /// <summary>
    /// The nine records the migrations seed are readable through the adapter with their stored
    /// supplier-master values - the half of issue #931's migration that only an adapter read proves,
    /// because the migration test reads the columns with SQL rather than through this projection.
    /// </summary>
    [Fact]
    public async Task The_seeded_master_list_is_readable_through_the_adapter()
    {
        await using var db = Context();
        var presets = await new EfSupplierPresetStore(db).ListOrderedByNameAsync(CancellationToken.None);

        var coles = presets.Single(preset => preset.Name == "Coles");
        Assert.True(coles.ServesNationwide);
        Assert.Empty(coles.ServedStates);
        Assert.True(coles.PricesIncludeGst);
        Assert.Equal(SupplierPriceConnector.Varnmill, coles.PriceConnector);
        Assert.Equal("coles", coles.PriceConnectorKey);

        var jbMetro = presets.Single(preset => preset.Name == "JB Metro SA & NT");
        Assert.False(jbMetro.ServesNationwide);
        Assert.Equal(["SA", "NT"], jbMetro.ServedStates);
        Assert.False(jbMetro.PricesIncludeGst);
        Assert.Equal(SupplierPriceConnector.JbMetro, jbMetro.PriceConnector);
        Assert.Null(jbMetro.PriceConnectorKey);
    }

    [Fact]
    public async Task The_pre_check_compares_the_stored_normalised_name_and_can_exclude_one_row()
    {
        await using var db = Context();
        var store = new EfSupplierPresetStore(db);

        var coles = (await store.ListOrderedByNameAsync(CancellationToken.None))
            .Single(preset => preset.Name == "Coles");

        Assert.True(await store.ExistsWithNormalisedNameAsync("COLES", null, CancellationToken.None));
        Assert.False(await store.ExistsWithNormalisedNameAsync(
            SupplierPresetPolicy.NormalisedName(UnusedName),
            null,
            CancellationToken.None));

        // Excluding the row itself is what lets an edit keep its own name.
        Assert.False(await store.ExistsWithNormalisedNameAsync("COLES", coles.Id, CancellationToken.None));
        Assert.True(
            await store.ExistsWithNormalisedNameAsync("COLES", coles.Id + 1000, CancellationToken.None));
    }

    [Fact]
    public async Task The_index_refuses_a_duplicate_and_the_adapter_recognises_that_refusal()
    {
        await using var db = Context();
        var store = new EfSupplierPresetStore(db);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => store.AddAsync(
            NewPreset("coles"),
            CancellationToken.None));

        Assert.True(
            store.IsDuplicateName(error),
            $"The adapter must recognise the normalised-name index refusing a duplicate. It saw: {error}");
    }

    /// <summary>
    /// Any other failure is left untranslated: a caller must never be told "that name is taken"
    /// because some unrelated constraint or fault occurred.
    /// </summary>
    [Fact]
    public async Task No_other_failure_is_reported_as_a_duplicate_name()
    {
        await using var db = Context();
        var store = new EfSupplierPresetStore(db);

        Assert.False(store.IsDuplicateName(new InvalidOperationException("something else")));
        Assert.False(store.IsDuplicateName(
            new DbUpdateException("wrapped", new SqliteException("UNIQUE constraint failed: Products.Name", 19))));

        await Task.CompletedTask;
    }

    /// <summary>
    /// The acceptance criterion in full: the pre-check passes and the index refuses, and the caller
    /// is answered with the same <c>409</c> the pre-check would have given - never a <c>500</c>, and
    /// never a second row.
    ///
    /// <para><see cref="BlindPreCheckStore"/> is the race, made deterministic: it is the real adapter
    /// with its pre-check answering "no such name", which is exactly what the losing request of two
    /// concurrent creates sees.</para>
    /// </summary>
    [Fact]
    public async Task A_create_whose_pre_check_missed_the_duplicate_is_still_a_conflict()
    {
        await using var db = Context();
        var store = new BlindPreCheckStore(new EfSupplierPresetStore(db));
        var audit = new RecordingAudit();
        var create = new CreateSupplierPreset(
            store,
            new FixedActorAccessor(),
            audit,
            new FixedClock(Now));

        var error = await Assert.ThrowsAsync<DomainConflictException>(() =>
            create.Handle(Submission("  COLES  "), CancellationToken.None));

        Assert.Equal(SupplierPresetPolicy.DuplicateNameMessage, error.Message);

        // One row, and no audit event: nothing was committed, so nothing is recorded.
        await using var verify = Context();
        Assert.Single(await verify.SupplierPresets.Where(p => p.NormalisedName == "COLES").ToListAsync());
        Assert.Empty(audit.Entries);
    }

    /// <summary>The same race on an edit, with the same answer.</summary>
    [Fact]
    public async Task An_edit_whose_pre_check_missed_the_duplicate_is_still_a_conflict()
    {
        await using var db = Context();
        var inner = new EfSupplierPresetStore(db);
        var woolworths = (await inner.ListOrderedByNameAsync(CancellationToken.None))
            .Single(preset => preset.Name == "Woolworths");

        var audit = new RecordingAudit();
        var update = new UpdateSupplierPreset(
            new BlindPreCheckStore(inner),
            new FixedActorAccessor(),
            audit,
            new FixedClock(Now));

        var error = await Assert.ThrowsAsync<DomainConflictException>(() =>
            update.Handle(woolworths.Id, Submission("coles"), isEnabled: true, CancellationToken.None));

        Assert.Equal(SupplierPresetPolicy.DuplicateNameMessage, error.Message);
        Assert.Empty(audit.Entries);

        await using var verify = Context();
        Assert.Equal(
            "Woolworths",
            (await verify.SupplierPresets.AsNoTracking().SingleAsync(p => p.Id == woolworths.Id)).Name);
    }

    /// <summary>
    /// A storable preset with the supplier-master values already through their policy, which is the
    /// state the port takes them in. Nationwide, GST-inclusive and with no connector by default,
    /// because these tests are about the index and the conflict rather than about a market.
    /// </summary>
    private static NewSupplierPreset NewPreset(
        string name,
        bool servesNationwide = true,
        string servedStates = "",
        bool pricesIncludeGst = true,
        SupplierPriceConnector? connector = null,
        string? connectorKey = null) =>
        new(
            name,
            SupplierPresetPolicy.NormalisedName(name),
            null,
            servesNationwide,
            servedStates,
            pricesIncludeGst,
            connector,
            connectorKey,
            Now);

    /// <summary>
    /// What a Super Admin submits, before any rule has run. Nationwide and GST-inclusive, so a test
    /// about the duplicate-name race is refused for the name and never for its market.
    /// </summary>
    private static SupplierPresetSubmission Submission(string? name) =>
        new(
            name,
            null,
            ServesNationwide: true,
            ServedStates: null,
            PricesIncludeGst: true,
            PriceConnector: null,
            PriceConnectorKey: null);

    /// <summary>
    /// The real adapter with a blind pre-check: every other call is delegated, so the insert, the
    /// index and the exception translation under test are all the real ones.
    /// </summary>
    private sealed class BlindPreCheckStore : ISupplierPresetStore
    {
        private readonly ISupplierPresetStore _inner;

        public BlindPreCheckStore(ISupplierPresetStore inner) => _inner = inner;

        public Task<bool> ExistsWithNormalisedNameAsync(
            string normalisedName,
            int? excludingId,
            CancellationToken cancellationToken) => Task.FromResult(false);

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

    private sealed class RecordingAudit : ISupplierPresetAudit
    {
        public List<SupplierPresetAuditEntry> Entries { get; } = [];

        public void Record(SupplierPresetAuditEntry entry) => Entries.Add(entry);
    }

    private sealed class FixedActorAccessor : IAuthenticatedActorAccessor
    {
        public ActorIdentityResult GetCurrentActor()
        {
            Assert.True(ActorIdentity.TryCreate(
                "99999999-9999-9999-9999-999999999999",
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                out var actor));

            return ActorIdentityResult.Identified(actor!);
        }
    }

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTime utcNow) => UtcNow = utcNow;

        public DateTime UtcNow { get; }
    }
}
