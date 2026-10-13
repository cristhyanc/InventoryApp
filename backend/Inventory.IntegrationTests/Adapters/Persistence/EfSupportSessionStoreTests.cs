using Inventory.Application.SupportSessions;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

// The entity and the Application's reported session share a concept in different layers. The row is
// what this adapter's tests seed and verify, so it gets the alias.
using SupportSessionRow = Inventory.Infrastructure.Models.SupportSession;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The support-session adapter of issue #550 against relational SQLite, with the real migration
/// history.
///
/// <para>The session table is one of the five structural non-tenant-owned tables, so this adapter
/// names the actor or the business in every statement and there is no query filter behind it. That
/// makes these tests the whole of the isolation proof for this table rather than a check on a central
/// mechanism: two businesses and two administrators throughout, so every read and every end can be
/// shown to reach exactly the named rows and no others - whatever scope the context itself happens to
/// carry, which for a platform-admin request is a denied one.</para>
///
/// <para>They also pin the three properties a fake cannot prove: that the ends really are conditional
/// statements in the database, that the session-id hash is unique globally, and that this adapter
/// reaches nothing tenant-owned under the denied scope its caller actually runs with.</para>
/// </summary>
public class EfSupportSessionStoreTests
{
    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string AdministratorOid = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string OtherAdministratorOid = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const int FirstBusinessId = 1;
    private const int SecondBusinessId = 2;

    private static readonly DateTime StartedAt = new(2026, 10, 13, 1, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = StartedAt.AddMinutes(10);

    /// <summary>
    /// A started session is stored against the business the adapter was given, with the actor, the
    /// reason, both instants and the hash it was handed - and comes back with the key the database
    /// assigned. The id's plaintext never reaches the adapter at all, so the row cannot hold one.
    /// </summary>
    [Fact]
    public async Task It_stores_the_session_against_the_business_and_actor_it_was_given()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            // The scope a platform-admin request really carries: denied. Nothing about this adapter
            // depends on a resolved business, which is the whole reason the table carries no filter.
            await using var db = TestAppDbContext.Denied(options);

            var stored = await new EfSupportSessionStore(db).StartAsync(
                new NewSupportSession(
                    Administrator(AdministratorOid),
                    FirstBusinessId,
                    "Investigating the reported dashboard error",
                    SupportSessionId.Hash("an-id-nobody-else-sees"),
                    StartedAt,
                    SupportSessionPolicy.ExpiresAt(StartedAt)),
                CancellationToken.None);

            Assert.True(stored.Key > 0);
            Assert.Equal(FirstBusinessId, stored.BusinessId);
            Assert.Null(stored.EndedAtUtc);

            await using var verify = TestAppDbContext.Unrestricted(options);
            var row = await verify.SupportSessions.AsNoTracking().SingleAsync();
            Assert.Equal(FirstBusinessId, row.BusinessId);
            Assert.Equal(SupportSessionId.Hash("an-id-nobody-else-sees"), row.SessionIdHash);
            // The identity as ActorIdentity normalised it, which is the one spelling this
            // application uses everywhere.
            Assert.Equal(Tid, row.ActorDirectoryTenantId, ignoreCase: true);
            Assert.Equal(AdministratorOid, row.ActorObjectId, ignoreCase: true);
            Assert.Equal("Investigating the reported dashboard error", row.Reason);
            Assert.Equal(StartedAt, row.StartedAtUtc);
            Assert.Equal(StartedAt.AddMinutes(60), row.ExpiresAtUtc);
            Assert.Null(row.EndedAtUtc);
        }
    }

    /// <summary>
    /// The active read answers about one administrator and nobody else, and it answers nothing for an
    /// expired session, an ended one, or an administrator with none. The instants come back as UTC,
    /// which is what every comparison above this adapter depends on.
    /// </summary>
    [Fact]
    public async Task It_reads_the_one_active_session_of_the_actor_it_was_given()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await SeedAsync(options, FirstBusinessId, AdministratorOid);
            await SeedAsync(options, SecondBusinessId, OtherAdministratorOid);

            await using var db = TestAppDbContext.Denied(options);
            var store = new EfSupportSessionStore(db);

            var mine = await store.FindActiveForActorAsync(
                Administrator(AdministratorOid),
                Now,
                CancellationToken.None);

            Assert.NotNull(mine);
            Assert.Equal(FirstBusinessId, mine!.BusinessId);
            Assert.Equal(DateTimeKind.Utc, mine.StartedAtUtc.Kind);
            Assert.Equal(DateTimeKind.Utc, mine.ExpiresAtUtc.Kind);

            // The other direction, which is what makes the actor in the statement the boundary rather
            // than a formality: the other administrator reads their own session and not this one.
            var theirs = await store.FindActiveForActorAsync(
                Administrator(OtherAdministratorOid),
                Now,
                CancellationToken.None);
            Assert.Equal(SecondBusinessId, theirs!.BusinessId);

            // And an administrator who was never granted one reads nothing.
            Assert.Null(await store.FindActiveForActorAsync(
                Administrator("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                Now,
                CancellationToken.None));
        }
    }

    /// <summary>
    /// Expiry is derived, not swept: the same stored row is active before its expiry instant and
    /// inactive at it, with nothing having run in between and no column written.
    /// </summary>
    [Fact]
    public async Task A_session_stops_being_read_as_active_at_its_expiry_instant()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            var key = await SeedAsync(options, FirstBusinessId, AdministratorOid);
            var expiresAt = SupportSessionPolicy.ExpiresAt(StartedAt);

            await using var db = TestAppDbContext.Denied(options);
            var store = new EfSupportSessionStore(db);

            Assert.NotNull(await store.FindActiveForActorAsync(
                Administrator(AdministratorOid),
                expiresAt.AddTicks(-1),
                CancellationToken.None));
            Assert.Null(await store.FindActiveForActorAsync(
                Administrator(AdministratorOid),
                expiresAt,
                CancellationToken.None));

            // Nothing was written to make that true, which is what "no sweep" means: the row still
            // records no decision to end it.
            await using var verify = TestAppDbContext.Unrestricted(options);
            Assert.Null((await verify.SupportSessions.AsNoTracking().SingleAsync(row => row.Id == key))
                .EndedAtUtc);
        }
    }

    /// <summary>
    /// Ending by actor writes the ended instant on that administrator's active session, names only
    /// that column, and leaves another administrator's session - and an already-ended one - exactly as
    /// they were. The returned rows are the ones that were written, which is what the audit trail is
    /// built from.
    /// </summary>
    [Fact]
    public async Task Ending_by_actor_closes_that_administrators_session_and_nobody_elses()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            var mine = await SeedAsync(options, FirstBusinessId, AdministratorOid);
            var theirs = await SeedAsync(options, SecondBusinessId, OtherAdministratorOid);
            var alreadyEnded = await SeedAsync(
                options,
                FirstBusinessId,
                AdministratorOid,
                endedAtUtc: StartedAt.AddMinutes(1));

            await using var db = TestAppDbContext.Denied(options);

            var ended = await new EfSupportSessionStore(db).EndAllForActorAsync(
                Administrator(AdministratorOid),
                Now,
                CancellationToken.None);

            var closed = Assert.Single(ended);
            Assert.Equal(mine, closed.Key);
            Assert.Equal(Now, closed.EndedAtUtc);

            await using var verify = TestAppDbContext.Unrestricted(options);
            Assert.Equal(Now, (await Row(verify, mine)).EndedAtUtc);
            Assert.Null((await Row(verify, theirs)).EndedAtUtc);
            // An end records a decision at an instant: re-stamping a session that was already closed
            // would make the record claim something that did not happen.
            Assert.Equal(StartedAt.AddMinutes(1), (await Row(verify, alreadyEnded)).EndedAtUtc);

            // And the reason, the expiry and the hash are untouched - an end names one column.
            var row = await Row(verify, mine);
            Assert.Equal("Investigating the reported dashboard error", row.Reason);
            Assert.Equal(SupportSessionPolicy.ExpiresAt(StartedAt), row.ExpiresAtUtc);
            Assert.NotEqual(string.Empty, row.SessionIdHash);
        }
    }

    /// <summary>
    /// Ending by business closes every administrator's active session for that business and nothing
    /// of another business's - which is what a deactivation depends on, and the reason the statement
    /// takes the business the transition read rather than a caller's value.
    /// </summary>
    [Fact]
    public async Task Ending_by_business_closes_every_session_of_that_business_only()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            var mine = await SeedAsync(options, FirstBusinessId, AdministratorOid);
            var anothers = await SeedAsync(options, FirstBusinessId, OtherAdministratorOid);
            var otherBusiness = await SeedAsync(
                options,
                SecondBusinessId,
                "cccccccc-cccc-cccc-cccc-cccccccccccc");

            await using var db = TestAppDbContext.Denied(options);

            var ended = await new EfSupportSessionStore(db).EndAllForBusinessAsync(
                BusinessId.From(FirstBusinessId),
                Now,
                CancellationToken.None);

            Assert.Equal([mine, anothers], ended.Select(session => session.Key));
            Assert.All(ended, session => Assert.Equal(Now, session.EndedAtUtc));

            await using var verify = TestAppDbContext.Unrestricted(options);
            Assert.Equal(Now, (await Row(verify, mine)).EndedAtUtc);
            Assert.Equal(Now, (await Row(verify, anothers)).EndedAtUtc);
            Assert.Null((await Row(verify, otherBusiness)).EndedAtUtc);
        }
    }

    /// <summary>
    /// Ending when nothing is active writes nothing and reports nothing - including for a session
    /// that has expired, which is the state a deactivation an hour later finds. That is what makes a
    /// repeated end, and a repeated deactivation, idempotent.
    /// </summary>
    [Fact]
    public async Task Ending_when_nothing_is_active_writes_nothing()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            var expired = await SeedAsync(options, FirstBusinessId, AdministratorOid);
            var afterExpiry = SupportSessionPolicy.ExpiresAt(StartedAt).AddMinutes(1);

            await using var db = TestAppDbContext.Denied(options);
            var store = new EfSupportSessionStore(db);

            Assert.Empty(await store.EndAllForActorAsync(
                Administrator(AdministratorOid),
                afterExpiry,
                CancellationToken.None));
            Assert.Empty(await store.EndAllForBusinessAsync(
                BusinessId.From(FirstBusinessId),
                afterExpiry,
                CancellationToken.None));

            await using var verify = TestAppDbContext.Unrestricted(options);
            Assert.Null((await Row(verify, expired)).EndedAtUtc);
        }
    }

    /// <summary>
    /// The session-id hash is unique across the whole table, which the database enforces: two
    /// sessions for different businesses and different administrators cannot share one. This is the
    /// deliberate opposite of the invite token's per-business uniqueness - a session id is the
    /// server's own secret, and the request that presents one carries no business to disambiguate it
    /// with.
    /// </summary>
    [Fact]
    public async Task The_session_id_hash_is_unique_across_every_business()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            var hash = SupportSessionId.Hash("the-same-id-twice");
            await SeedAsync(options, FirstBusinessId, AdministratorOid, sessionIdHash: hash);

            await using var db = TestAppDbContext.Unrestricted(options);
            db.SupportSessions.Add(NewRow(SecondBusinessId, OtherAdministratorOid, sessionIdHash: hash));

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    /// <summary>
    /// The adapter's caller runs with a denied scope, and that still protects everything else: a
    /// tenant-owned read through the same context sees nothing, and a tenant-owned write through it
    /// is refused. The session table is reachable, and the rest of the database is not - which is the
    /// whole of what this slice adds to a platform-admin request's reach.
    /// </summary>
    [Fact]
    public async Task The_denied_scope_this_adapter_runs_under_still_reaches_nothing_tenant_owned()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using (var seed = TestAppDbContext.Unrestricted(options))
            {
                seed.Products.Add(new Product { BusinessId = FirstBusinessId, Name = "Mars Bar" });
                await seed.SaveChangesAsync();
            }

            await using var db = TestAppDbContext.Denied(options);

            // The session the adapter writes is readable; the product in the same database is not.
            await new EfSupportSessionStore(db).StartAsync(
                new NewSupportSession(
                    Administrator(AdministratorOid),
                    FirstBusinessId,
                    "Investigating the reported dashboard error",
                    SupportSessionId.Hash("an-id-nobody-else-sees"),
                    StartedAt,
                    SupportSessionPolicy.ExpiresAt(StartedAt)),
                CancellationToken.None);

            Assert.NotNull(await new EfSupportSessionStore(db).FindActiveForActorAsync(
                Administrator(AdministratorOid),
                Now,
                CancellationToken.None));
            Assert.Empty(await db.Products.ToListAsync());

            db.Products.Add(new Product { Name = "Snickers" });
            await Assert.ThrowsAsync<CrossBusinessAccessException>(() => db.SaveChangesAsync());
        }
    }

    #region The id-to-session lookup a support request makes (issue #551)

    /// <summary>
    /// The lookup a support request needs: the presented id, hashed, finds that administrator's own
    /// active session - and the row it answers with still carries no copy of the hash, let alone the
    /// id, because the projection does not read that column.
    /// </summary>
    [Fact]
    public async Task The_presented_id_finds_the_callers_own_active_session()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            const string sessionId = "the-id-the-start-returned-once";
            var key = await SeedAsync(
                options, FirstBusinessId, AdministratorOid, sessionIdHash: SupportSessionId.Hash(sessionId));
            await using var db = TestAppDbContext.Denied(options);

            var found = await new EfSupportSessionStore(db).FindActiveForActorByIdHashAsync(
                Administrator(AdministratorOid),
                SupportSessionId.Hash(sessionId),
                Now,
                CancellationToken.None);

            Assert.NotNull(found);
            Assert.Equal(key, found!.Key);
            Assert.Equal(FirstBusinessId, found.BusinessId);
            Assert.Equal(SupportSessionPolicy.ExpiresAt(StartedAt), found.ExpiresAtUtc);
            Assert.Equal(DateTimeKind.Utc, found.ExpiresAtUtc.Kind);
        }
    }

    /// <summary>
    /// The four ways the lookup finds nothing, each of them a condition of the statement rather than
    /// a check made on its result: another administrator presenting a leaked id, the caller's own id
    /// after it expired, after it was ended, and an id that was never issued at all.
    ///
    /// <para>The leaked-id case is the one that matters most. A session is visible and usable only to
    /// the administrator it was granted to, so a copied id is not a session somebody else is
    /// <em>refused</em> - it is a session they cannot find.</para>
    /// </summary>
    [Fact]
    public async Task The_presented_id_finds_nothing_for_anybody_else_or_once_it_is_over()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            const string sessionId = "the-id-the-start-returned-once";
            var hash = SupportSessionId.Hash(sessionId);
            await SeedAsync(options, FirstBusinessId, AdministratorOid, sessionIdHash: hash);
            await using var db = TestAppDbContext.Denied(options);
            var store = new EfSupportSessionStore(db);

            // Another administrator with the same id in their hand.
            Assert.Null(await store.FindActiveForActorByIdHashAsync(
                Administrator(OtherAdministratorOid), hash, Now, CancellationToken.None));

            // The owner's own id, after the expiry instant - inclusive, exactly as IsActive is.
            Assert.Null(await store.FindActiveForActorByIdHashAsync(
                Administrator(AdministratorOid),
                hash,
                SupportSessionPolicy.ExpiresAt(StartedAt),
                CancellationToken.None));

            // An id that names no row.
            Assert.Null(await store.FindActiveForActorByIdHashAsync(
                Administrator(AdministratorOid),
                SupportSessionId.Hash("an-id-nothing-ever-issued"),
                Now,
                CancellationToken.None));
        }
    }

    /// <summary>
    /// An ended session is over for the lookup too, which is what makes the end a real revocation
    /// rather than a flag somewhere: the row still exists, and presenting its id finds nothing.
    /// </summary>
    [Fact]
    public async Task An_ended_sessions_id_finds_nothing()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            var hash = SupportSessionId.Hash("an-id-that-was-ended");
            await SeedAsync(
                options,
                FirstBusinessId,
                AdministratorOid,
                endedAtUtc: StartedAt.AddMinutes(5),
                sessionIdHash: hash);
            await using var db = TestAppDbContext.Denied(options);

            Assert.Null(await new EfSupportSessionStore(db).FindActiveForActorByIdHashAsync(
                Administrator(AdministratorOid), hash, Now, CancellationToken.None));
        }
    }

    /// <summary>
    /// A blank hash is a programming error, not a lookup: hashing nothing would otherwise produce a
    /// perfectly valid-looking key, and the only way to reach it is a caller that forgot to check
    /// what the request presented.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_hash_is_refused_rather_than_queried(string hash)
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Denied(options);

            await Assert.ThrowsAsync<ArgumentException>(
                () => new EfSupportSessionStore(db).FindActiveForActorByIdHashAsync(
                    Administrator(AdministratorOid), hash, Now, CancellationToken.None));
        }
    }

    #endregion

    private static async Task<(SqliteConnection Connection, DbContextOptions<AppDbContext> Options)> CreateSqliteAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var setup = TestAppDbContext.Unrestricted(options);
        await setup.Database.MigrateAsync();
        setup.Businesses.AddRange(
            NewBusiness(FirstBusinessId, "Vending Co"),
            NewBusiness(SecondBusinessId, "Other Vending Co"));
        await setup.SaveChangesAsync();
        return (connection, options);
    }

    private static async Task<int> SeedAsync(
        DbContextOptions<AppDbContext> options,
        int businessId,
        string objectId,
        DateTime? endedAtUtc = null,
        string? sessionIdHash = null)
    {
        await using var db = TestAppDbContext.Unrestricted(options);
        var row = NewRow(businessId, objectId, endedAtUtc, sessionIdHash);
        db.SupportSessions.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private static SupportSessionRow NewRow(
        int businessId,
        string objectId,
        DateTime? endedAtUtc = null,
        string? sessionIdHash = null) =>
        new()
        {
            BusinessId = businessId,
            SessionIdHash = sessionIdHash
                ?? SupportSessionId.Hash($"{businessId}-{objectId}-{Guid.NewGuid()}"),
            ActorDirectoryTenantId = Tid.ToUpperInvariant(),
            ActorObjectId = objectId.ToUpperInvariant(),
            Reason = "Investigating the reported dashboard error",
            StartedAtUtc = StartedAt,
            ExpiresAtUtc = SupportSessionPolicy.ExpiresAt(StartedAt),
            EndedAtUtc = endedAtUtc,
        };

    private static Business NewBusiness(int id, string name) =>
        new()
        {
            Id = id,
            Name = name,
            TimeZoneId = "Australia/Sydney",
            Status = BusinessStatus.Active,
            CreatedAtUtc = StartedAt,
        };

    private static Task<SupportSessionRow> Row(AppDbContext db, int key) =>
        db.SupportSessions.AsNoTracking().SingleAsync(row => row.Id == key);

    private static ActorIdentity Administrator(string objectId)
    {
        Assert.True(ActorIdentity.TryCreate(Tid, objectId, out var identity));
        return identity!;
    }
}
