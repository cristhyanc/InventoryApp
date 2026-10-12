using Inventory.Application.Members;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

// The entity and the Application's reported invite share a name, in different layers. The row is
// what this adapter's tests seed and verify, so it gets the alias.
using MemberInviteRow = Inventory.Infrastructure.Models.MemberInvite;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The Owner-facing invite adapter of issue #531 against relational SQLite, with the real migration
/// history.
///
/// <para>Since the reviewed ownership decision on issue #532 the invite table is one of the four
/// structural non-tenant-owned tables, so this adapter names the business in every statement and
/// there is no query filter behind it. That makes these tests the whole of the isolation proof for
/// this table rather than a check on a central mechanism: two businesses throughout, so a read, a
/// lookup, a cancellation and the bulk cancellation can each be shown to reach the named business's
/// rows and no other's - whatever scope the context itself happens to carry.</para>
///
/// <para>They also pin the two properties a fake cannot prove: that the cancellations really are
/// single conditional statements in the database, and that the token hash is unique per business
/// rather than globally.</para>
/// </summary>
public class EfMemberInviteStoreTests
{
    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string Oid = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const int FirstBusinessId = 1;
    private const int SecondBusinessId = 2;

    private static readonly BusinessId First = BusinessId.From(FirstBusinessId);
    private static readonly BusinessId Second = BusinessId.From(SecondBusinessId);

    private static readonly DateTime CreatedAt = new(2026, 10, 12, 1, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Now = CreatedAt.AddDays(1);

    /// <summary>
    /// A created invite is stored against the business the adapter was given - which since issue
    /// #532 is the owner being written rather than one the enforcer stamps - as pending, with the
    /// hash it was handed, and comes back with the key the database assigned.
    /// </summary>
    [Fact]
    public async Task It_stores_the_invite_against_the_business_it_was_given()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.For(options, FirstBusinessId);

            var created = await new EfMemberInviteStore(db).CreateAsync(
                First,
                new NewMemberInvite(
                    BusinessRole.Manager,
                    "Tuesday run",
                    MemberInviteToken.Hash("a-token-nobody-else-sees"),
                    Identity(),
                    CreatedAt,
                    MemberInvitePolicy.ExpiresAt(CreatedAt)),
                CancellationToken.None);

            Assert.True(created.InviteId > 0);
            Assert.Equal(MemberInviteStatus.Pending, created.StoredStatus);
            Assert.Equal(MemberInvitePolicy.ExpiresAt(CreatedAt), created.ExpiresAtUtc);

            await using var verify = TestAppDbContext.Unrestricted(options);
            var row = await verify.MemberInvites.AsNoTracking().SingleAsync();
            Assert.Equal(FirstBusinessId, row.BusinessId);
            Assert.Equal(MemberInviteToken.Hash("a-token-nobody-else-sees"), row.TokenHash);
            // The creating identity as ActorIdentity normalised it, which is the same pair in the
            // one spelling this application uses everywhere.
            Assert.Equal(Tid, row.CreatedByDirectoryTenantId, ignoreCase: true);
            Assert.Equal(Oid, row.CreatedByObjectId, ignoreCase: true);
            Assert.Equal(MemberInviteStatus.Pending, row.Status);
            Assert.Null(row.CancelledAtUtc);
            Assert.Null(row.UsedAtUtc);
            Assert.Null(row.RedeemedByMembershipId);
        }
    }

    /// <summary>
    /// The read sees one business's invites, in creation order, and a lookup by another business's
    /// invite key answers nothing - so another business's key is "not found" rather than reachable.
    /// Neither read loads the token hash: proving an invite exists must not read the one value that
    /// has to stay in the database.
    /// </summary>
    [Fact]
    public async Task It_reads_only_the_invites_of_the_business_it_was_given()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            var mine = await SeedAsync(options, FirstBusinessId, note: "Mine");
            var alsoMine = await SeedAsync(options, FirstBusinessId, note: "Also mine");
            var theirs = await SeedAsync(options, SecondBusinessId, note: "Theirs");

            await using var db = TestAppDbContext.For(options, FirstBusinessId);
            var store = new EfMemberInviteStore(db);

            var listed = await store.ListAsync(First, CancellationToken.None);
            Assert.Equal([mine, alsoMine], listed.Select(invite => invite.InviteId));
            Assert.Equal(["Mine", "Also mine"], listed.Select(invite => invite.Note));

            Assert.NotNull(await store.FindAsync(First, mine, CancellationToken.None));
            Assert.Null(await store.FindAsync(First, theirs, CancellationToken.None));

            // And the other direction, which is what makes the id in the statement the boundary
            // rather than a formality: the same invite key is found under its own business.
            Assert.NotNull(await store.FindAsync(Second, theirs, CancellationToken.None));
            Assert.Null(await store.FindAsync(Second, mine, CancellationToken.None));
        }
    }

    /// <summary>
    /// The business id the adapter is given is what confines every statement, and the context's own
    /// scope is not. That is the whole ownership decision of issue #532 stated as a test: this table
    /// carries no query filter, so a scope cannot be what protects it - and the id only ever comes
    /// from tenancy resolution (the Owner-facing use cases) or from an invite row (the redemption),
    /// never from a request.
    ///
    /// <para>It is asserted over an unrestricted and a denied context alike, because those are the
    /// two contexts whose filtered behaviour used to differ: a reviewer reading this adapter should
    /// see that it no longer depends on either.</para>
    /// </summary>
    [Fact]
    public async Task The_business_id_in_the_statement_confines_it_whatever_scope_the_context_carries()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            var mine = await SeedAsync(options, FirstBusinessId);
            var theirs = await SeedAsync(options, SecondBusinessId);

            foreach (var scoped in new Func<AppDbContext>[]
            {
                () => TestAppDbContext.Unrestricted(options),
                () => TestAppDbContext.Denied(options),
                () => TestAppDbContext.For(options, SecondBusinessId),
            })
            {
                await using var db = scoped();
                var store = new EfMemberInviteStore(db);

                var listed = await store.ListAsync(First, CancellationToken.None);
                Assert.Equal([mine], listed.Select(invite => invite.InviteId));
                Assert.Null(await store.FindAsync(First, theirs, CancellationToken.None));

                // The conditional write is confined the same way: the other business's live invite
                // is not matched by a statement naming this one, so nothing is written for it.
                Assert.False(await store.CancelAsync(First, theirs, Now, CancellationToken.None));
            }

            await using (var verify = TestAppDbContext.Unrestricted(options))
            {
                Assert.All(
                    await verify.MemberInvites.AsNoTracking().ToListAsync(),
                    invite => Assert.Equal(MemberInviteStatus.Pending, invite.Status));
            }

            // And the bulk cancellation closes the named business's invites and only those, from a
            // context scoped to the other one - again, the id in the statement is the boundary.
            await using (var db = TestAppDbContext.For(options, FirstBusinessId))
            {
                Assert.Equal(
                    1,
                    await new EfMemberInviteStore(db).CancelAllLiveAsync(Second, Now, CancellationToken.None));
            }

            await using var rows = TestAppDbContext.Unrestricted(options);
            Assert.Equal(
                MemberInviteStatus.Pending,
                (await rows.MemberInvites.AsNoTracking().SingleAsync(invite => invite.Id == mine)).Status);
            Assert.Equal(
                MemberInviteStatus.Cancelled,
                (await rows.MemberInvites.AsNoTracking().SingleAsync(invite => invite.Id == theirs)).Status);
        }
    }

    /// <summary>
    /// The cancellation is one conditional statement: it cancels a live invite, and it declines -
    /// writing nothing - for an invite that is used, already cancelled, expired, in an undeclared
    /// state, or another business's. That is what makes the decision and the write one operation
    /// rather than a read the caller then trusts.
    /// </summary>
    [Fact]
    public async Task It_cancels_only_a_live_invite_of_the_business_it_was_given()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            var live = await SeedAsync(options, FirstBusinessId);
            var used = await SeedAsync(options, FirstBusinessId, status: MemberInviteStatus.Used, usedAtUtc: CreatedAt);
            var already = await SeedAsync(
                options,
                FirstBusinessId,
                status: MemberInviteStatus.Cancelled,
                cancelledAtUtc: CreatedAt);
            var expired = await SeedAsync(options, FirstBusinessId, expiresAtUtc: Now.AddTicks(-1));
            var undeclared = await SeedAsync(options, FirstBusinessId, statusValue: 99);
            var theirs = await SeedAsync(options, SecondBusinessId);

            await using var db = TestAppDbContext.For(options, FirstBusinessId);
            var store = new EfMemberInviteStore(db);

            Assert.True(await store.CancelAsync(First, live, Now, CancellationToken.None));
            Assert.False(await store.CancelAsync(First, used, Now, CancellationToken.None));
            Assert.False(await store.CancelAsync(First, already, Now, CancellationToken.None));
            Assert.False(await store.CancelAsync(First, expired, Now, CancellationToken.None));
            Assert.False(await store.CancelAsync(First, undeclared, Now, CancellationToken.None));
            Assert.False(await store.CancelAsync(First, theirs, Now, CancellationToken.None));

            await using var verify = TestAppDbContext.Unrestricted(options);
            var rows = await verify.MemberInvites.AsNoTracking().ToDictionaryAsync(invite => invite.Id);

            Assert.Equal(MemberInviteStatus.Cancelled, rows[live].Status);
            Assert.Equal(Now, rows[live].CancelledAtUtc);
            Assert.Equal(MemberInviteStatus.Used, rows[used].Status);
            Assert.Null(rows[used].CancelledAtUtc);
            Assert.Equal(CreatedAt, rows[already].CancelledAtUtc);
            Assert.Equal(MemberInviteStatus.Pending, rows[expired].Status);
            Assert.Null(rows[expired].CancelledAtUtc);
            Assert.Equal((MemberInviteStatus)99, rows[undeclared].Status);
            Assert.Equal(MemberInviteStatus.Pending, rows[theirs].Status);
            Assert.Null(rows[theirs].CancelledAtUtc);
        }
    }

    /// <summary>
    /// The bulk cancellation #509's deactivation calls: every live invite of the named business is
    /// closed at one instant, every other state is left exactly as it is, and the other business's
    /// invites are untouched. A second call changes nothing, so a restarted deactivation is safe.
    /// </summary>
    [Fact]
    public async Task It_cancels_every_live_invite_of_the_business_it_was_given_and_no_others()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            var live = await SeedAsync(options, FirstBusinessId);
            var alsoLive = await SeedAsync(options, FirstBusinessId);
            var used = await SeedAsync(options, FirstBusinessId, status: MemberInviteStatus.Used, usedAtUtc: CreatedAt);
            var expired = await SeedAsync(options, FirstBusinessId, expiresAtUtc: Now.AddTicks(-1));
            var theirs = await SeedAsync(options, SecondBusinessId);

            await using var db = TestAppDbContext.For(options, FirstBusinessId);
            var store = new EfMemberInviteStore(db);

            Assert.Equal(2, await store.CancelAllLiveAsync(First, Now, CancellationToken.None));
            Assert.Equal(0, await store.CancelAllLiveAsync(First, Now.AddMinutes(1), CancellationToken.None));

            await using var verify = TestAppDbContext.Unrestricted(options);
            var rows = await verify.MemberInvites.AsNoTracking().ToDictionaryAsync(invite => invite.Id);

            Assert.Equal(MemberInviteStatus.Cancelled, rows[live].Status);
            Assert.Equal(Now, rows[live].CancelledAtUtc);
            Assert.Equal(MemberInviteStatus.Cancelled, rows[alsoLive].Status);
            Assert.Equal(Now, rows[alsoLive].CancelledAtUtc);
            Assert.Equal(MemberInviteStatus.Used, rows[used].Status);
            Assert.Null(rows[used].CancelledAtUtc);
            Assert.Equal(MemberInviteStatus.Pending, rows[expired].Status);
            Assert.Equal(MemberInviteStatus.Pending, rows[theirs].Status);
            Assert.Null(rows[theirs].CancelledAtUtc);
        }
    }

    /// <summary>
    /// The bulk cancellation joins a transaction its caller already opened, which is what lets
    /// #509's deactivation commit or roll back the two together: a rolled-back deactivation leaves
    /// every invite exactly as it was.
    /// </summary>
    [Fact]
    public async Task A_rolled_back_caller_transaction_takes_the_cancellations_with_it()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            var live = await SeedAsync(options, FirstBusinessId);

            await using (var db = TestAppDbContext.For(options, FirstBusinessId))
            {
                await using var transaction = await db.Database.BeginTransactionAsync();
                Assert.Equal(
                    1,
                    await new EfMemberInviteStore(db).CancelAllLiveAsync(First, Now, CancellationToken.None));
                await transaction.RollbackAsync();
            }

            await using var verify = TestAppDbContext.Unrestricted(options);
            var row = await verify.MemberInvites.AsNoTracking().SingleAsync(invite => invite.Id == live);
            Assert.Equal(MemberInviteStatus.Pending, row.Status);
            Assert.Null(row.CancelledAtUtc);
        }
    }

    /// <summary>
    /// The token hash is unique per business, not globally: two businesses must be able to hold the
    /// same value - the repository-wide rule for a uniqueness constraint over an externally shaped
    /// value - while one business cannot hold the same invite token twice.
    /// </summary>
    [Fact]
    public async Task The_token_hash_is_unique_within_a_business_and_not_across_businesses()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            var hash = MemberInviteToken.Hash("the-same-token");

            await SeedAsync(options, FirstBusinessId, tokenHash: hash);
            await SeedAsync(options, SecondBusinessId, tokenHash: hash);

            await using var db = TestAppDbContext.For(options, FirstBusinessId);
            await Assert.ThrowsAsync<DbUpdateException>(() =>
                new EfMemberInviteStore(db).CreateAsync(
                    First,
                    new NewMemberInvite(
                        BusinessRole.Manager,
                        null,
                        hash,
                        Identity(),
                        CreatedAt,
                        MemberInvitePolicy.ExpiresAt(CreatedAt)),
                    CancellationToken.None));
        }
    }

    /// <summary>
    /// The redeemed-by column cannot name a membership that does not exist: the foreign key the
    /// migration added is what keeps the link between an invite and the access it granted honest,
    /// whatever the redemption of #532 is asked to write.
    /// </summary>
    [Fact]
    public async Task The_redeemed_by_membership_must_exist()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Unrestricted(options);
            db.MemberInvites.Add(NewInvite(FirstBusinessId, redeemedByMembershipId: 999_999));

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

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
        string? note = null,
        MemberInviteStatus status = MemberInviteStatus.Pending,
        int? statusValue = null,
        DateTime? expiresAtUtc = null,
        DateTime? usedAtUtc = null,
        DateTime? cancelledAtUtc = null,
        string? tokenHash = null)
    {
        await using var db = TestAppDbContext.Unrestricted(options);
        var invite = NewInvite(
            businessId,
            note,
            statusValue is { } undeclared ? (MemberInviteStatus)undeclared : status,
            expiresAtUtc,
            usedAtUtc,
            cancelledAtUtc,
            tokenHash: tokenHash);
        db.MemberInvites.Add(invite);
        await db.SaveChangesAsync();
        return invite.Id;
    }

    private static MemberInviteRow NewInvite(
        int businessId,
        string? note = null,
        MemberInviteStatus status = MemberInviteStatus.Pending,
        DateTime? expiresAtUtc = null,
        DateTime? usedAtUtc = null,
        DateTime? cancelledAtUtc = null,
        int? redeemedByMembershipId = null,
        string? tokenHash = null) =>
        new()
        {
            BusinessId = businessId,
            Role = BusinessRole.Manager,
            Note = note,
            TokenHash = tokenHash ?? MemberInviteToken.Hash(Guid.NewGuid().ToString()),
            Status = status,
            CreatedByDirectoryTenantId = Tid,
            CreatedByObjectId = Oid,
            CreatedAtUtc = CreatedAt,
            ExpiresAtUtc = expiresAtUtc ?? MemberInvitePolicy.ExpiresAt(CreatedAt),
            UsedAtUtc = usedAtUtc,
            CancelledAtUtc = cancelledAtUtc,
            RedeemedByMembershipId = redeemedByMembershipId,
        };

    private static Business NewBusiness(int id, string name) =>
        new()
        {
            Id = id,
            Name = name,
            TimeZoneId = "Australia/Sydney",
            Status = BusinessStatus.Active,
            CreatedAtUtc = CreatedAt,
        };

    private static ActorIdentity Identity()
    {
        Assert.True(ActorIdentity.TryCreate(Tid, Oid, out var identity));
        return identity!;
    }
}
