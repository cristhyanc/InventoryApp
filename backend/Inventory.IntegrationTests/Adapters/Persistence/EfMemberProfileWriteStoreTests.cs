using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The dedicated narrow write path for the display-only member profile (issue #524), on relational
/// SQLite rather than InMemory: what it has to prove is SQL - that the update names two columns and
/// one row, that "only where it differs" is decided in the statement rather than by a read this
/// adapter did earlier, and that a <c>NULL</c> column counts as different from a value.
///
/// <para>Every context here carries a <em>denied</em> business scope, because that is the situation
/// the refresh actually runs in: it happens in its own service scope after the request has gone, so
/// no business is resolved. The write works because <c>BusinessMembership</c> is one of the three
/// structural non-tenant-owned tables, and the tests below are what prove it still cannot touch any
/// row but the caller's own.</para>
/// </summary>
public class EfMemberProfileWriteStoreTests
{
    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string Oid = "22222222-2222-2222-2222-222222222222";
    private const string OtherOid = "33333333-3333-3333-3333-333333333333";

    private static readonly DateTime CreatedAt = new(2026, 2, 10, 4, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime StatusChangedAt = new(2026, 3, 11, 5, 0, 0, DateTimeKind.Utc);

    private static async Task<(SqliteConnection Connection, DbContextOptions<AppDbContext> Options)> CreateSqliteAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var setup = TestAppDbContext.Unrestricted(options);
        await setup.Database.EnsureCreatedAsync();
        return (connection, options);
    }

    private static ActorIdentity Actor(string oid = Oid, string tid = Tid)
    {
        Assert.True(ActorIdentity.TryCreate(tid, oid, out var actor));
        return actor!;
    }

    private static MemberProfile Profile(
        string? displayName = "Ada Lovelace",
        string? email = "ada@vending.example") =>
        MemberProfile.Create(displayName, email);

    private static async Task<int> SeedAsync(
        DbContextOptions<AppDbContext> options,
        string oid = Oid,
        bool isActive = true,
        int businessId = 1,
        string? displayName = null,
        string? email = null)
    {
        await using var db = TestAppDbContext.Unrestricted(options);

        if (!await db.Businesses.AnyAsync(business => business.Id == businessId))
        {
            db.Businesses.Add(new Business
            {
                Id = businessId,
                Name = $"Vending Co {businessId}",
                TimeZoneId = "Australia/Sydney",
                IsActive = true,
                CreatedAtUtc = CreatedAt,
            });
        }

        var membership = new BusinessMembership
        {
            BusinessId = businessId,
            DirectoryTenantId = Tid,
            ObjectId = oid,
            Role = BusinessRole.Manager,
            IsActive = isActive,
            CreatedAtUtc = CreatedAt,
            StatusChangedAtUtc = StatusChangedAt,
            DisplayName = displayName,
            Email = email,
        };

        db.BusinessMemberships.Add(membership);
        await db.SaveChangesAsync();
        return membership.Id;
    }

    private static async Task<int> RefreshAsync(
        DbContextOptions<AppDbContext> options,
        ActorIdentity identity,
        MemberProfile profile)
    {
        // Denied, exactly as the drain's own scope is: the refresh never has a current business.
        await using var db = TestAppDbContext.Denied(options);

        return await new EfMemberProfileWriteStore(db)
            .RefreshActiveMembershipProfileAsync(identity, profile, CancellationToken.None);
    }

    private static async Task<BusinessMembership> ReadAsync(DbContextOptions<AppDbContext> options, int membershipId)
    {
        await using var db = TestAppDbContext.Unrestricted(options);
        return await db.BusinessMemberships.AsNoTracking().SingleAsync(m => m.Id == membershipId);
    }

    /// <summary>
    /// The refresh itself: a membership that has never stored either value gets both, from the
    /// claims the caller's own validated token reported.
    /// </summary>
    [Fact]
    public async Task A_membership_with_no_stored_profile_is_given_the_tokens_name_and_email()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            var membershipId = await SeedAsync(options);

            Assert.Equal(1, await RefreshAsync(options, Actor(), Profile()));

            var membership = await ReadAsync(options, membershipId);
            Assert.Equal("Ada Lovelace", membership.DisplayName);
            Assert.Equal("ada@vending.example", membership.Email);
        }
    }

    /// <summary>
    /// A changed name or email replaces what is stored, and either half may change on its own.
    /// </summary>
    [Theory]
    [InlineData("Ada Byron", "ada@vending.example")]
    [InlineData("Ada Lovelace", "ada.byron@vending.example")]
    [InlineData("Ada Byron", "ada.byron@vending.example")]
    public async Task A_changed_claim_replaces_the_stored_value(string displayName, string email)
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            var membershipId = await SeedAsync(
                options,
                displayName: "Ada Lovelace",
                email: "ada@vending.example");

            Assert.Equal(1, await RefreshAsync(options, Actor(), Profile(displayName, email)));

            var membership = await ReadAsync(options, membershipId);
            Assert.Equal(displayName, membership.DisplayName);
            Assert.Equal(email, membership.Email);
        }
    }

    /// <summary>
    /// A claim that disappeared from the token clears the stored value rather than leaving a stale
    /// one: the row says what the token last reported, and "no longer reported" is an answer.
    /// </summary>
    [Fact]
    public async Task A_claim_that_is_no_longer_reported_clears_the_stored_value()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            var membershipId = await SeedAsync(
                options,
                displayName: "Ada Lovelace",
                email: "ada@vending.example");

            Assert.Equal(1, await RefreshAsync(options, Actor(), Profile(email: null)));

            var membership = await ReadAsync(options, membershipId);
            Assert.Equal("Ada Lovelace", membership.DisplayName);
            Assert.Null(membership.Email);
        }
    }

    /// <summary>
    /// The steady state: the stored profile already matches the token, so the statement changes no
    /// row at all. This is what makes an hourly refresh cost nothing on a single-writer database
    /// for a value that changes once in a person's employment - and it is decided inside the
    /// <c>UPDATE</c>, so two refreshes racing cannot both decide a write is needed.
    /// </summary>
    [Fact]
    public async Task An_unchanged_profile_changes_no_row()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await SeedAsync(options, displayName: "Ada Lovelace", email: "ada@vending.example");

            Assert.Equal(0, await RefreshAsync(options, Actor(), Profile()));
        }
    }

    /// <summary>
    /// Nothing but the two display columns moves. The role, the revocation flag, the creation and
    /// state instants and the identity itself are all left exactly as they were - a display refresh
    /// must never be able to change what a membership means.
    /// </summary>
    [Fact]
    public async Task Nothing_but_the_two_display_columns_changes()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            var membershipId = await SeedAsync(options);
            var before = await ReadAsync(options, membershipId);

            Assert.Equal(1, await RefreshAsync(options, Actor(), Profile()));

            var after = await ReadAsync(options, membershipId);
            Assert.Equal(before.BusinessId, after.BusinessId);
            Assert.Equal(before.DirectoryTenantId, after.DirectoryTenantId);
            Assert.Equal(before.ObjectId, after.ObjectId);
            Assert.Equal(BusinessRole.Manager, after.Role);
            Assert.True(after.IsActive);
            Assert.Equal(CreatedAt, after.CreatedAtUtc);
            Assert.Equal(StatusChangedAt, after.StatusChangedAtUtc);
        }
    }

    /// <summary>
    /// A revoked membership is not refreshed. The person is no longer a member, and a revoked row
    /// is history: writing today's claims into it would quietly edit what the history says.
    /// </summary>
    [Fact]
    public async Task A_revoked_membership_is_not_refreshed()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            var membershipId = await SeedAsync(options, isActive: false);

            Assert.Equal(0, await RefreshAsync(options, Actor(), Profile()));

            var membership = await ReadAsync(options, membershipId);
            Assert.Null(membership.DisplayName);
            Assert.Null(membership.Email);
        }
    }

    /// <summary>
    /// A person who moved from one business to another has a revoked row and an active one. Only
    /// the active one is refreshed, so the revoked row keeps the details it was revoked with.
    /// </summary>
    [Fact]
    public async Task Only_the_active_membership_of_an_identity_with_history_is_refreshed()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            var revoked = await SeedAsync(
                options,
                isActive: false,
                businessId: 1,
                displayName: "A. Lovelace",
                email: "a.lovelace@old.example");
            var active = await SeedAsync(options, businessId: 2);

            Assert.Equal(1, await RefreshAsync(options, Actor(), Profile()));

            var refreshed = await ReadAsync(options, active);
            Assert.Equal("Ada Lovelace", refreshed.DisplayName);

            var history = await ReadAsync(options, revoked);
            Assert.Equal("A. Lovelace", history.DisplayName);
            Assert.Equal("a.lovelace@old.example", history.Email);
        }
    }

    /// <summary>
    /// Another person's membership is untouched, including one in another business. The identity in
    /// the statement is the one the request authenticated as, and nothing a request supplies
    /// chooses whose row is written.
    /// </summary>
    [Fact]
    public async Task Another_identitys_membership_is_untouched()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            var mine = await SeedAsync(options, businessId: 1);
            var theirs = await SeedAsync(options, oid: OtherOid, businessId: 2);

            Assert.Equal(1, await RefreshAsync(options, Actor(), Profile()));

            Assert.Equal("Ada Lovelace", (await ReadAsync(options, mine)).DisplayName);

            var other = await ReadAsync(options, theirs);
            Assert.Null(other.DisplayName);
            Assert.Null(other.Email);
        }
    }

    /// <summary>
    /// The same object id in a different Entra directory is a different person, so neither half of
    /// the identity pair may match on its own.
    /// </summary>
    [Fact]
    public async Task The_same_object_id_in_another_directory_is_not_this_member()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            var membershipId = await SeedAsync(options);

            var elsewhere = Actor(tid: "44444444-4444-4444-4444-444444444444");
            Assert.Equal(0, await RefreshAsync(options, elsewhere, Profile()));

            Assert.Null((await ReadAsync(options, membershipId)).DisplayName);
        }
    }

    /// <summary>
    /// A membership row entered by hand in different GUID casing is still this member's row: the
    /// identity columns are <c>NOCASE</c>, exactly as the resolution lookup relies on.
    /// </summary>
    [Fact]
    public async Task A_membership_stored_in_different_casing_is_still_refreshed()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            var membershipId = await SeedAsync(options, oid: Oid.ToLowerInvariant());

            Assert.Equal(1, await RefreshAsync(options, Actor(), Profile()));

            Assert.Equal("Ada Lovelace", (await ReadAsync(options, membershipId)).DisplayName);
        }
    }

    /// <summary>
    /// An identity with no membership at all - a token that was valid when the request ran and whose
    /// membership has since been deleted - writes nothing and is not an error.
    /// </summary>
    [Fact]
    public async Task An_identity_with_no_membership_writes_nothing()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await SeedAsync(options, oid: OtherOid);

            Assert.Equal(0, await RefreshAsync(options, Actor(), Profile()));
        }
    }
}
