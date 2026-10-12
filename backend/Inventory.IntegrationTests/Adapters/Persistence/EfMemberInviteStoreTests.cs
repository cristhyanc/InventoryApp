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
/// The invite adapter of issue #531 against relational SQLite, with the real migration history.
///
/// An invite is ordinary tenant-owned data, so this adapter writes no business predicate of its
/// own: the <c>AppDbContext</c> query filters scope its reads and <c>BusinessOwnershipEnforcer</c>
/// stamps its insert. These tests are where that is proved to be enough - two businesses
/// throughout, so a read, a cancellation and the bulk cancellation can each be shown to reach one
/// business's rows and no other's - and where the two properties a fake cannot prove are pinned:
/// that the cancellations really are single conditional statements in the database, and that an
/// unscoped context cannot run one.
/// </summary>
public class EfMemberInviteStoreTests
{
    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string Oid = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const int FirstBusinessId = 1;
    private const int SecondBusinessId = 2;

    private static readonly DateTime CreatedAt = new(2026, 10, 12, 1, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Now = CreatedAt.AddDays(1);

    /// <summary>
    /// A created invite is stamped with the caller's own business, stored as pending with the hash
    /// it was handed, and comes back with the key the database assigned.
    /// </summary>
    [Fact]
    public async Task It_stamps_the_callers_business_and_stores_the_hash_it_was_given()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.For(options, FirstBusinessId);

            var created = await new EfMemberInviteStore(db).CreateAsync(
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
    /// A caller whose membership did not resolve writes nothing: the central ownership enforcement
    /// refuses the insert rather than letting an unowned invite into the table.
    /// </summary>
    [Fact]
    public async Task A_denied_caller_cannot_create_an_invite()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Denied(options);

            await Assert.ThrowsAsync<CrossBusinessAccessException>(() =>
                new EfMemberInviteStore(db).CreateAsync(
                    new NewMemberInvite(
                        BusinessRole.Manager,
                        null,
                        MemberInviteToken.Hash("t"),
                        Identity(),
                        CreatedAt,
                        MemberInvitePolicy.ExpiresAt(CreatedAt)),
                    CancellationToken.None));

            await using var verify = TestAppDbContext.Unrestricted(options);
            Assert.Empty(await verify.MemberInvites.ToListAsync());
        }
    }

    /// <summary>
    /// The read sees one business's invites, in creation order, and a lookup by another business's
    /// invite key answers nothing - so another business's key is "not found" rather than reachable.
    /// Neither read loads the token hash: proving an invite exists must not read the one value that
    /// has to stay in the database.
    /// </summary>
    [Fact]
    public async Task It_reads_only_the_current_businesss_invites()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            var mine = await SeedAsync(options, FirstBusinessId, note: "Mine");
            var alsoMine = await SeedAsync(options, FirstBusinessId, note: "Also mine");
            var theirs = await SeedAsync(options, SecondBusinessId, note: "Theirs");

            await using var db = TestAppDbContext.For(options, FirstBusinessId);
            var store = new EfMemberInviteStore(db);

            var listed = await store.ListAsync(CancellationToken.None);
            Assert.Equal([mine, alsoMine], listed.Select(invite => invite.InviteId));
            Assert.Equal(["Mine", "Also mine"], listed.Select(invite => invite.Note));

            Assert.NotNull(await store.FindAsync(mine, CancellationToken.None));
            Assert.Null(await store.FindAsync(theirs, CancellationToken.None));
        }
    }

    [Fact]
    public async Task A_denied_caller_reads_no_invite_at_all()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            var mine = await SeedAsync(options, FirstBusinessId);

            await using var db = TestAppDbContext.Denied(options);
            var store = new EfMemberInviteStore(db);

            Assert.Empty(await store.ListAsync(CancellationToken.None));
            Assert.Null(await store.FindAsync(mine, CancellationToken.None));
        }
    }

    /// <summary>
    /// The cancellation is one conditional statement: it cancels a live invite, and it declines -
    /// writing nothing - for an invite that is used, already cancelled, expired, in an undeclared
    /// state, or another business's. That is what makes the decision and the write one operation
    /// rather than a read the caller then trusts.
    /// </summary>
    [Fact]
    public async Task It_cancels_only_a_live_invite_of_the_current_business()
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

            Assert.True(await store.CancelAsync(live, Now, CancellationToken.None));
            Assert.False(await store.CancelAsync(used, Now, CancellationToken.None));
            Assert.False(await store.CancelAsync(already, Now, CancellationToken.None));
            Assert.False(await store.CancelAsync(expired, Now, CancellationToken.None));
            Assert.False(await store.CancelAsync(undeclared, Now, CancellationToken.None));
            Assert.False(await store.CancelAsync(theirs, Now, CancellationToken.None));

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
    /// The bulk cancellation #509's deactivation calls: every live invite of the current business
    /// is closed at one instant, every other state is left exactly as it is, and the other
    /// business's invites are untouched. A second call changes nothing, so a restarted
    /// deactivation is safe.
    /// </summary>
    [Fact]
    public async Task It_cancels_every_live_invite_of_the_current_business_and_no_others()
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

            Assert.Equal(2, await store.CancelAllLiveAsync(Now, CancellationToken.None));
            Assert.Equal(0, await store.CancelAllLiveAsync(Now.AddMinutes(1), CancellationToken.None));

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
                Assert.Equal(1, await new EfMemberInviteStore(db).CancelAllLiveAsync(Now, CancellationToken.None));
                await transaction.RollbackAsync();
            }

            await using var verify = TestAppDbContext.Unrestricted(options);
            var row = await verify.MemberInvites.AsNoTracking().SingleAsync(invite => invite.Id == live);
            Assert.Equal(MemberInviteStatus.Pending, row.Status);
            Assert.Null(row.CancelledAtUtc);
        }
    }

    /// <summary>
    /// Neither cancellation may run without a single resolved business. <c>ExecuteUpdate</c> runs
    /// in the database and does not pass through <c>BusinessOwnershipEnforcer</c>, so an unscoped
    /// statement would carry no business predicate at all and cancel every business's invites -
    /// which is exactly what this guard refuses, for a deliberately unscoped context and for a
    /// caller whose membership failed alike.
    /// </summary>
    [Fact]
    public async Task No_unscoped_or_denied_caller_may_cancel_anything()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            var mine = await SeedAsync(options, FirstBusinessId);
            var theirs = await SeedAsync(options, SecondBusinessId);

            await using (var unscoped = TestAppDbContext.Unrestricted(options))
            {
                var store = new EfMemberInviteStore(unscoped);
                await Assert.ThrowsAsync<CrossBusinessAccessException>(() =>
                    store.CancelAsync(mine, Now, CancellationToken.None));
                await Assert.ThrowsAsync<CrossBusinessAccessException>(() =>
                    store.CancelAllLiveAsync(Now, CancellationToken.None));
            }

            await using (var denied = TestAppDbContext.Denied(options))
            {
                var store = new EfMemberInviteStore(denied);
                await Assert.ThrowsAsync<CrossBusinessAccessException>(() =>
                    store.CancelAsync(mine, Now, CancellationToken.None));
                await Assert.ThrowsAsync<CrossBusinessAccessException>(() =>
                    store.CancelAllLiveAsync(Now, CancellationToken.None));
            }

            await using var verify = TestAppDbContext.Unrestricted(options);
            Assert.All(
                await verify.MemberInvites.AsNoTracking().ToListAsync(),
                invite => Assert.Equal(MemberInviteStatus.Pending, invite.Status));
            Assert.Equal(2, await verify.MemberInvites.CountAsync(invite => invite.Id == mine || invite.Id == theirs));
        }
    }

    /// <summary>
    /// The token hash is unique per business, not globally: two businesses must be able to hold the
    /// same value - the repository-wide rule for uniqueness on tenant-owned data - while one
    /// business cannot hold the same invite token twice.
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
    /// whatever the redemption of #532 is later asked to write.
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
