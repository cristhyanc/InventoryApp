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
/// The redemption adapter of issue #532 against relational SQLite, with the real migration history.
///
/// <para>Every test here runs the adapter over a <strong>denied</strong> context, because that is
/// the only scope a redemption request ever carries: the caller has no membership, so nothing
/// resolved a business for them. Proving the adapter works under that scope is proving the ownership
/// decision - a filtered table could not be read at all here - and proving it reaches only the
/// business it was given is proving the boundary that replaced the filter.</para>
///
/// <para>Two businesses throughout, each with its own invite and its own member, so every lookup and
/// every write can be shown to stop at the one it names.</para>
/// </summary>
public class EfMemberInviteRedemptionStoreTests
{
    private const int FirstBusinessId = 1;
    private const int SecondBusinessId = 2;

    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string JoinerOid = "22222222-2222-2222-2222-222222222222";
    private const string OtherOid = "33333333-3333-3333-3333-333333333333";

    private static readonly BusinessId First = BusinessId.From(FirstBusinessId);
    private static readonly BusinessId Second = BusinessId.From(SecondBusinessId);

    private static readonly DateTime CreatedAt = new(2026, 10, 12, 1, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Now = CreatedAt.AddDays(1);

    #region Finding the invite by its token

    /// <summary>
    /// The lookup a redemption starts from: the token's hash finds its invite, with the inviting
    /// business's status in the same answer, under a denied scope. Nothing else identifies the
    /// invite, and the business id comes back from the row rather than from the caller.
    /// </summary>
    [Fact]
    public async Task It_finds_an_invite_by_its_token_hash_under_a_denied_scope()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            var inviteId = await SeedInviteAsync(options, FirstBusinessId, "the-link", BusinessRole.Manager);

            await using var db = TestAppDbContext.Denied(options);

            var found = await new EfMemberInviteRedemptionStore(db)
                .FindByTokenHashAsync(MemberInviteToken.Hash("the-link"), CancellationToken.None);

            Assert.NotNull(found);
            Assert.Equal(inviteId, found.InviteId);
            Assert.Equal(First, found.BusinessId);
            Assert.Equal(BusinessRole.Manager, found.Role);
            Assert.Equal(MemberInviteStatus.Pending, found.StoredStatus);
            Assert.Equal(MemberInvitePolicy.ExpiresAt(CreatedAt), found.ExpiresAtUtc);
            Assert.Equal(BusinessStatus.Active, found.BusinessStatus);
        }
    }

    /// <summary>
    /// A token nothing answers to is simply not found, and so is the plaintext of a token that does
    /// exist: the row holds a hash, so a lookup by the token itself finds nothing - which is the
    /// property that makes a database copy useless.
    /// </summary>
    [Fact]
    public async Task A_token_nothing_answers_to_is_not_found()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await SeedInviteAsync(options, FirstBusinessId, "the-link", BusinessRole.Manager);

            await using var db = TestAppDbContext.Denied(options);
            var store = new EfMemberInviteRedemptionStore(db);

            Assert.Null(await store.FindByTokenHashAsync(
                MemberInviteToken.Hash("not-a-link"),
                CancellationToken.None));
            Assert.Null(await store.FindByTokenHashAsync("the-link", CancellationToken.None));
        }
    }

    /// <summary>
    /// The lookup reports the stored status, role and business status exactly as they are - a closed
    /// invite, an undeclared role and a deactivated business all come back untouched - because the
    /// policy, not the adapter, is what decides they are unusable.
    /// </summary>
    [Fact]
    public async Task It_reports_a_closed_invite_and_a_closed_business_exactly_as_stored()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await SeedInviteAsync(
                options,
                SecondBusinessId,
                "closed",
                (BusinessRole)99,
                status: MemberInviteStatus.Used,
                usedAtUtc: CreatedAt);
            await SetBusinessStatusAsync(options, SecondBusinessId, BusinessStatus.Deactivated);

            await using var db = TestAppDbContext.Denied(options);

            var found = await new EfMemberInviteRedemptionStore(db)
                .FindByTokenHashAsync(MemberInviteToken.Hash("closed"), CancellationToken.None);

            Assert.NotNull(found);
            Assert.Equal(Second, found.BusinessId);
            Assert.Equal((BusinessRole)99, found.Role);
            Assert.Equal(MemberInviteStatus.Used, found.StoredStatus);
            Assert.Equal(BusinessStatus.Deactivated, found.BusinessStatus);
        }
    }

    /// <summary>
    /// Two businesses holding the same token hash is a state the per-business unique index
    /// deliberately allows, and it leaves "which business did this token mean?" with no answer. The
    /// lookup refuses it rather than picking one, exactly as an ambiguous membership is denied -
    /// which the caller reports as the one generic invalid-invite refusal.
    /// </summary>
    [Fact]
    public async Task A_token_hash_two_businesses_hold_is_refused_rather_than_resolved()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await SeedInviteAsync(options, FirstBusinessId, "shared", BusinessRole.Manager);
            await SeedInviteAsync(options, SecondBusinessId, "shared", BusinessRole.Owner);

            await using var db = TestAppDbContext.Denied(options);

            Assert.Null(await new EfMemberInviteRedemptionStore(db)
                .FindByTokenHashAsync(MemberInviteToken.Hash("shared"), CancellationToken.None));
        }
    }

    #endregion

    #region Finding the redeeming person's own membership

    /// <summary>
    /// The membership lookup is confined to the business it is given and to the identity it is
    /// given: another business's row for the same person, and this business's row for somebody else,
    /// are both not found.
    /// </summary>
    [Fact]
    public async Task The_membership_lookup_is_confined_to_one_business_and_one_identity()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            var mine = await SeedMembershipAsync(options, FirstBusinessId, JoinerOid, isActive: false);
            await SeedMembershipAsync(options, SecondBusinessId, JoinerOid, isActive: true);
            await SeedMembershipAsync(options, FirstBusinessId, OtherOid, isActive: true);

            await using var db = TestAppDbContext.Denied(options);
            var store = new EfMemberInviteRedemptionStore(db);

            var found = await store.FindMembershipAsync(First, Identity(JoinerOid), CancellationToken.None);
            Assert.NotNull(found);
            Assert.Equal(mine, found.Value.MembershipId);
            Assert.False(found.Value.IsActive);

            // The same person in the other business is that business's row, not this one's.
            var elsewhere = await store.FindMembershipAsync(Second, Identity(JoinerOid), CancellationToken.None);
            Assert.NotNull(elsewhere);
            Assert.NotEqual(mine, elsewhere.Value.MembershipId);
            Assert.True(elsewhere.Value.IsActive);
        }
    }

    [Fact]
    public async Task A_business_that_records_nothing_for_the_identity_answers_nothing()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await SeedMembershipAsync(options, FirstBusinessId, OtherOid, isActive: true);

            await using var db = TestAppDbContext.Denied(options);

            Assert.Null(await new EfMemberInviteRedemptionStore(db)
                .FindMembershipAsync(First, Identity(JoinerOid), CancellationToken.None));
        }
    }

    #endregion

    #region Creating and reactivating the membership

    /// <summary>
    /// The membership is created in the business it is given, active, with the invite's role and the
    /// display values the caller's token reported - under a denied scope, which is the scope every
    /// redemption carries. <c>BusinessMembership</c> is non-tenant-owned, so the ownership enforcer
    /// has no stamp to apply and the business id written is the one this call named.
    /// </summary>
    [Fact]
    public async Task It_creates_an_active_membership_in_the_business_it_was_given()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Denied(options);

            var membershipId = await new EfMemberInviteRedemptionStore(db).CreateActiveMembershipAsync(
                First,
                Identity(JoinerOid),
                MemberProfile.Create("Dana Doe", "dana@example.test"),
                BusinessRole.Manager,
                Now,
                CancellationToken.None);

            await using var verify = TestAppDbContext.Unrestricted(options);
            var row = await verify.BusinessMemberships.AsNoTracking().SingleAsync();

            Assert.Equal(membershipId, row.Id);
            Assert.Equal(FirstBusinessId, row.BusinessId);
            Assert.Equal(Tid, row.DirectoryTenantId, ignoreCase: true);
            Assert.Equal(JoinerOid, row.ObjectId, ignoreCase: true);
            Assert.Equal(BusinessRole.Manager, row.Role);
            Assert.True(row.IsActive);
            Assert.Equal("Dana Doe", row.DisplayName);
            Assert.Equal("dana@example.test", row.Email);
            // One instant for the creation and the state change alike (issue #522).
            Assert.Equal(Now, row.CreatedAtUtc);
            Assert.Equal(Now, row.StatusChangedAtUtc);
        }
    }

    [Fact]
    public async Task A_token_that_reported_no_display_values_creates_a_membership_with_none()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Denied(options);

            await new EfMemberInviteRedemptionStore(db).CreateActiveMembershipAsync(
                First,
                Identity(JoinerOid),
                MemberProfile.None,
                BusinessRole.Operator,
                Now,
                CancellationToken.None);

            await using var verify = TestAppDbContext.Unrestricted(options);
            var row = await verify.BusinessMemberships.AsNoTracking().SingleAsync();

            Assert.Null(row.DisplayName);
            Assert.Null(row.Email);
        }
    }

    /// <summary>
    /// The filtered one-active-membership index of issue #522 still refuses a second active
    /// membership for one identity when the insert comes from here. The redemption's eligibility
    /// check is what normally refuses that, and this is the database backing it up.
    /// </summary>
    [Fact]
    public async Task A_second_active_membership_for_one_identity_is_refused_by_the_database()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await SeedMembershipAsync(options, SecondBusinessId, JoinerOid, isActive: true);

            await using var db = TestAppDbContext.Denied(options);

            await Assert.ThrowsAsync<DbUpdateException>(() =>
                new EfMemberInviteRedemptionStore(db).CreateActiveMembershipAsync(
                    First,
                    Identity(JoinerOid),
                    MemberProfile.None,
                    BusinessRole.Manager,
                    Now,
                    CancellationToken.None));
        }
    }

    /// <summary>
    /// The reactivation is one conditional statement: it brings a revoked row back with the invite's
    /// role and the state instant, and it declines - writing nothing - for a row that is already
    /// active, for another business's row, and for a key that exists nowhere.
    /// </summary>
    [Fact]
    public async Task It_reactivates_only_a_revoked_row_of_the_business_it_was_given()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            var revoked = await SeedMembershipAsync(options, FirstBusinessId, JoinerOid, isActive: false);
            var active = await SeedMembershipAsync(options, FirstBusinessId, OtherOid, isActive: true);
            var elsewhere = await SeedMembershipAsync(options, SecondBusinessId, "44444444-4444-4444-4444-444444444444", isActive: false);

            await using var db = TestAppDbContext.Denied(options);
            var store = new EfMemberInviteRedemptionStore(db);

            Assert.False(await store.ReactivateMembershipAsync(
                First, active, BusinessRole.Owner, MemberProfile.None, Now, CancellationToken.None));
            Assert.False(await store.ReactivateMembershipAsync(
                First, elsewhere, BusinessRole.Owner, MemberProfile.None, Now, CancellationToken.None));
            Assert.False(await store.ReactivateMembershipAsync(
                First, 999_999, BusinessRole.Owner, MemberProfile.None, Now, CancellationToken.None));
            Assert.True(await store.ReactivateMembershipAsync(
                First, revoked, BusinessRole.Owner, MemberProfile.None, Now, CancellationToken.None));

            await using var verify = TestAppDbContext.Unrestricted(options);
            var rows = await verify.BusinessMemberships.AsNoTracking().ToDictionaryAsync(row => row.Id);

            Assert.True(rows[revoked].IsActive);
            Assert.Equal(BusinessRole.Owner, rows[revoked].Role);
            Assert.Equal(Now, rows[revoked].StatusChangedAtUtc);

            // The declined ones are exactly as they were.
            Assert.Equal(BusinessRole.Owner, rows[active].Role);
            Assert.Equal(MembershipWriteRaceHarness.SeededAt, rows[active].StatusChangedAtUtc);
            Assert.False(rows[elsewhere].IsActive);
        }
    }

    /// <summary>
    /// A reactivation stores the display values the caller's current token reported, and a claim the
    /// token did not carry leaves the stored value alone rather than erasing it: a token that says
    /// nothing must not delete what an earlier one said (issue #524).
    /// </summary>
    [Fact]
    public async Task A_reactivation_refreshes_the_display_values_it_was_given_and_erases_none()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            var first = await SeedMembershipAsync(
                options,
                FirstBusinessId,
                JoinerOid,
                isActive: false,
                displayName: "Old Name",
                email: "old@example.test");
            var second = await SeedMembershipAsync(
                options,
                SecondBusinessId,
                OtherOid,
                isActive: false,
                displayName: "Kept Name",
                email: "kept@example.test");

            await using var db = TestAppDbContext.Denied(options);
            var store = new EfMemberInviteRedemptionStore(db);

            Assert.True(await store.ReactivateMembershipAsync(
                First,
                first,
                BusinessRole.Manager,
                MemberProfile.Create("New Name", email: null),
                Now,
                CancellationToken.None));
            Assert.True(await store.ReactivateMembershipAsync(
                Second,
                second,
                BusinessRole.Manager,
                MemberProfile.None,
                Now,
                CancellationToken.None));

            await using var verify = TestAppDbContext.Unrestricted(options);
            var rows = await verify.BusinessMemberships.AsNoTracking().ToDictionaryAsync(row => row.Id);

            Assert.Equal("New Name", rows[first].DisplayName);
            // The email the token did not report is the one that was already stored.
            Assert.Equal("old@example.test", rows[first].Email);

            Assert.Equal("Kept Name", rows[second].DisplayName);
            Assert.Equal("kept@example.test", rows[second].Email);
        }
    }

    #endregion

    #region Closing the invite

    /// <summary>
    /// The close is one conditional statement: it marks a live invite used against the membership it
    /// created, and it declines - writing nothing - for an invite that is used, cancelled, expired,
    /// in an undeclared state, or another business's.
    /// </summary>
    [Fact]
    public async Task It_marks_used_only_a_live_invite_of_the_business_it_was_given()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            var live = await SeedInviteAsync(options, FirstBusinessId, "live", BusinessRole.Manager);
            var used = await SeedInviteAsync(
                options, FirstBusinessId, "used", BusinessRole.Manager,
                status: MemberInviteStatus.Used, usedAtUtc: CreatedAt);
            var cancelled = await SeedInviteAsync(
                options, FirstBusinessId, "cancelled", BusinessRole.Manager,
                status: MemberInviteStatus.Cancelled, cancelledAtUtc: CreatedAt);
            var expired = await SeedInviteAsync(
                options, FirstBusinessId, "expired", BusinessRole.Manager,
                expiresAtUtc: Now.AddTicks(-1));
            var undeclared = await SeedInviteAsync(
                options, FirstBusinessId, "undeclared", BusinessRole.Manager,
                status: (MemberInviteStatus)99);
            var theirs = await SeedInviteAsync(options, SecondBusinessId, "theirs", BusinessRole.Manager);

            var membershipId = await SeedMembershipAsync(options, FirstBusinessId, JoinerOid, isActive: true);

            await using var db = TestAppDbContext.Denied(options);
            var store = new EfMemberInviteRedemptionStore(db);

            Assert.True(await store.MarkUsedAsync(First, live, membershipId, Now, CancellationToken.None));
            Assert.False(await store.MarkUsedAsync(First, used, membershipId, Now, CancellationToken.None));
            Assert.False(await store.MarkUsedAsync(First, cancelled, membershipId, Now, CancellationToken.None));
            Assert.False(await store.MarkUsedAsync(First, expired, membershipId, Now, CancellationToken.None));
            Assert.False(await store.MarkUsedAsync(First, undeclared, membershipId, Now, CancellationToken.None));
            Assert.False(await store.MarkUsedAsync(First, theirs, membershipId, Now, CancellationToken.None));

            await using var verify = TestAppDbContext.Unrestricted(options);
            var rows = await verify.MemberInvites.AsNoTracking().ToDictionaryAsync(invite => invite.Id);

            Assert.Equal(MemberInviteStatus.Used, rows[live].Status);
            Assert.Equal(Now, rows[live].UsedAtUtc);
            Assert.Equal(membershipId, rows[live].RedeemedByMembershipId);
            Assert.Null(rows[live].CancelledAtUtc);

            Assert.Equal(CreatedAt, rows[used].UsedAtUtc);
            Assert.Null(rows[used].RedeemedByMembershipId);
            Assert.Equal(MemberInviteStatus.Cancelled, rows[cancelled].Status);
            Assert.Null(rows[cancelled].UsedAtUtc);
            Assert.Equal(MemberInviteStatus.Pending, rows[expired].Status);
            Assert.Null(rows[expired].UsedAtUtc);
            Assert.Equal((MemberInviteStatus)99, rows[undeclared].Status);
            Assert.Equal(MemberInviteStatus.Pending, rows[theirs].Status);
            Assert.Null(rows[theirs].RedeemedByMembershipId);
        }
    }

    /// <summary>
    /// A redemption that is rolled back leaves no membership and no closed invite. It is the
    /// property the whole transaction exists for, asserted at the adapter level over the real
    /// database: the insert and both conditional updates go back together.
    /// </summary>
    [Fact]
    public async Task A_rolled_back_transaction_leaves_no_membership_and_no_used_invite()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            var inviteId = await SeedInviteAsync(options, FirstBusinessId, "the-link", BusinessRole.Manager);

            await using (var db = TestAppDbContext.Denied(options))
            {
                var store = new EfMemberInviteRedemptionStore(db);
                await using var transaction = await db.Database.BeginTransactionAsync();

                var membershipId = await store.CreateActiveMembershipAsync(
                    First, Identity(JoinerOid), MemberProfile.None, BusinessRole.Manager, Now, CancellationToken.None);
                Assert.True(await store.MarkUsedAsync(First, inviteId, membershipId, Now, CancellationToken.None));

                await transaction.RollbackAsync();
            }

            await using var verify = TestAppDbContext.Unrestricted(options);
            Assert.Empty(await verify.BusinessMemberships.AsNoTracking().ToListAsync());
            var invite = await verify.MemberInvites.AsNoTracking().SingleAsync();
            Assert.Equal(MemberInviteStatus.Pending, invite.Status);
            Assert.Null(invite.UsedAtUtc);
            Assert.Null(invite.RedeemedByMembershipId);
        }
    }

    #endregion

    #region Nothing tenant-owned is reachable

    /// <summary>
    /// The redemption's context is denied, and it stays denied: everything tenant-owned is still
    /// invisible to it, and a tenant-owned write through it is still refused. That is what "a marked
    /// endpoint reads no business data" means concretely for the one marked endpoint that writes.
    /// </summary>
    [Fact]
    public async Task The_denied_context_a_redemption_runs_on_still_reads_and_writes_no_business_data()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using (var setup = TestAppDbContext.Unrestricted(options))
            {
                setup.Categories.Add(new Category { BusinessId = FirstBusinessId, Name = "Confectionery" });
                await setup.SaveChangesAsync();
            }

            await using var db = TestAppDbContext.Denied(options);

            // The redemption's own two tables are reachable; the business's data is not.
            Assert.Empty(await db.Categories.AsNoTracking().ToListAsync());

            db.Categories.Add(new Category { Name = "Smuggled in" });
            await Assert.ThrowsAsync<CrossBusinessAccessException>(() => db.SaveChangesAsync());
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

    private static async Task SetBusinessStatusAsync(
        DbContextOptions<AppDbContext> options,
        int businessId,
        BusinessStatus status)
    {
        await using var db = TestAppDbContext.Unrestricted(options);
        (await db.Businesses.SingleAsync(business => business.Id == businessId)).Status = status;
        await db.SaveChangesAsync();
    }

    private static async Task<int> SeedInviteAsync(
        DbContextOptions<AppDbContext> options,
        int businessId,
        string token,
        BusinessRole role,
        MemberInviteStatus status = MemberInviteStatus.Pending,
        DateTime? expiresAtUtc = null,
        DateTime? usedAtUtc = null,
        DateTime? cancelledAtUtc = null)
    {
        await using var db = TestAppDbContext.Unrestricted(options);
        var invite = new MemberInviteRow
        {
            BusinessId = businessId,
            Role = role,
            TokenHash = MemberInviteToken.Hash(token),
            Status = status,
            CreatedByDirectoryTenantId = Tid,
            CreatedByObjectId = OtherOid,
            CreatedAtUtc = CreatedAt,
            ExpiresAtUtc = expiresAtUtc ?? MemberInvitePolicy.ExpiresAt(CreatedAt),
            UsedAtUtc = usedAtUtc,
            CancelledAtUtc = cancelledAtUtc,
        };

        db.MemberInvites.Add(invite);
        await db.SaveChangesAsync();
        return invite.Id;
    }

    private static async Task<int> SeedMembershipAsync(
        DbContextOptions<AppDbContext> options,
        int businessId,
        string objectId,
        bool isActive,
        string? displayName = null,
        string? email = null)
    {
        await using var db = TestAppDbContext.Unrestricted(options);
        var membership = new BusinessMembership
        {
            BusinessId = businessId,
            DirectoryTenantId = Tid,
            ObjectId = objectId,
            DisplayName = displayName,
            Email = email,
            Role = BusinessRole.Owner,
            IsActive = isActive,
            CreatedAtUtc = MembershipWriteRaceHarness.SeededAt,
            StatusChangedAtUtc = MembershipWriteRaceHarness.SeededAt,
        };

        db.BusinessMemberships.Add(membership);
        await db.SaveChangesAsync();
        return membership.Id;
    }

    private static Business NewBusiness(int id, string name) =>
        new()
        {
            Id = id,
            Name = name,
            TimeZoneId = "Australia/Sydney",
            Status = BusinessStatus.Active,
            CreatedAtUtc = CreatedAt,
        };

    private static ActorIdentity Identity(string objectId)
    {
        Assert.True(ActorIdentity.TryCreate(Tid, objectId, out var identity));
        return identity!;
    }
}
