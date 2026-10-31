using Inventory.Domain.Nayax;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational tests for the Super Admin's business directory and its two decisions (issue #544),
/// over a real migrated SQLite database and - this is the point - a <strong>denied</strong>
/// <c>BusinessScope</c>, which is the scope the platform administrator's request actually carries.
///
/// <para>SQLite rather than InMemory, because what has to be proven here is relational: that the
/// central tenant query filters really are in force on the very context this adapter reads
/// cross-business through, that its one <c>IgnoreQueryFilters</c> statement reaches exactly one
/// column of one table and nothing else, and that a tenant-owned write through the same context is
/// still refused.</para>
///
/// <para>Two businesses throughout, as AGENTS.md § Tenant ownership requires of any change to
/// ownership or filtering: the properties under test are about what one caller can reach across a
/// boundary, and a single-business fixture cannot express them.</para>
/// </summary>
public class EfPlatformBusinessDirectoryStoreTests
{
    private const string DirectoryTenantId = "11111111-1111-1111-1111-111111111111";
    private const string FirstOwnerObjectId = "22222222-2222-2222-2222-222222222222";
    private const string SecondOwnerObjectId = "33333333-3333-3333-3333-333333333333";
    private const string ManagerObjectId = "44444444-4444-4444-4444-444444444444";
    private const string Token = "nayax-lynx-access-token-value";

    private static readonly DateTime CreatedAt = new(2026, 2, 10, 4, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime AppliedAt = new(2026, 9, 1, 6, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime DecidedAt = new(2026, 10, 12, 3, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime LastTestedAt = new(2026, 10, 25, 2, 15, 0, DateTimeKind.Utc);

    #region The directory read

    /// <summary>
    /// The read every other endpoint in this application cannot make: every business, under a scope
    /// that resolved none of them, with pending applications first.
    /// </summary>
    [Fact]
    public async Task The_directory_lists_every_business_with_pending_applications_first()
    {
        await using var fixture = await DirectoryFixture.CreateAsync();

        var listed = await fixture.Store().ListAsync(CancellationToken.None);

        Assert.Equal(
            [fixture.PendingBusinessId, fixture.TradingBusinessId, fixture.RejectedBusinessId],
            listed.Select(business => business.BusinessId));
        Assert.Equal(
            [BusinessStatus.Pending, BusinessStatus.Active, BusinessStatus.Rejected],
            listed.Select(business => business.Status));
    }

    /// <summary>
    /// The directory fields, read off the rows: the application's own detail, its instants, and the
    /// count of memberships that are currently active. The trading business has two members and one
    /// revoked row, which is what tells "member count" from "active member count".
    /// </summary>
    [Fact]
    public async Task The_directory_reports_the_applications_detail_and_its_active_member_count()
    {
        await using var fixture = await DirectoryFixture.CreateAsync();

        var listed = await fixture.Store().ListAsync(CancellationToken.None);

        var pending = listed.Single(business => business.BusinessId == fixture.PendingBusinessId);
        Assert.Equal("Coastal Vending", pending.Name);
        Assert.Equal("owner@coastal.example.com", pending.ContactEmail);
        Assert.Equal("51824753556", pending.Abn);
        Assert.Equal(CreatedAt, pending.CreatedAtUtc);
        Assert.Equal(AppliedAt, pending.AppliedAtUtc);
        Assert.Null(pending.DecidedAtUtc);
        Assert.Equal(1, pending.ActiveMemberCount);

        var trading = listed.Single(business => business.BusinessId == fixture.TradingBusinessId);
        Assert.Equal(2, trading.ActiveMemberCount);
        Assert.Null(trading.AppliedAtUtc);

        var rejected = listed.Single(business => business.BusinessId == fixture.RejectedBusinessId);
        Assert.Equal(DecidedAt, rejected.DecidedAtUtc);
        Assert.Equal(0, rejected.ActiveMemberCount);
    }

    /// <summary>
    /// The Nayax flag is the existence of a connection row and nothing else - and it is read for a
    /// business this request resolved no scope for, which the central query filter would otherwise
    /// hide. The filter being in force is asserted beside it: the same context, asked the ordinary
    /// way, sees none of those rows.
    /// </summary>
    [Fact]
    public async Task The_directory_reports_whether_each_business_has_stored_nayax_credentials()
    {
        await using var fixture = await DirectoryFixture.CreateAsync();
        await fixture.SeedNayaxConnectionAsync(fixture.TradingBusinessId);

        var db = fixture.DeniedContext();
        var listed = await new EfPlatformBusinessDirectoryStore(db).ListAsync(CancellationToken.None);

        Assert.True(listed.Single(business => business.BusinessId == fixture.TradingBusinessId).NayaxConfigured);
        Assert.All(
            listed.Where(business => business.BusinessId != fixture.TradingBusinessId),
            business => Assert.False(business.NayaxConfigured));

        // The ordinary read through the very same context sees nothing, which is what makes the
        // adapter's single IgnoreQueryFilters statement the whole of the exception.
        Assert.Empty(await db.BusinessNayaxConnections.ToListAsync());
    }

    /// <summary>
    /// The stored connection status travels with the yes/no, for each of the four values, and it is
    /// read under the same denied scope through the same one widened projection (issue #983). A
    /// business with no connection row reads <see cref="NayaxConnectionStatus.NotConfigured"/>,
    /// which is what the absence of that row means - so "nothing stored" and "stored but unusable"
    /// stay apart here exactly as they do on the row.
    /// </summary>
    [Theory]
    [InlineData(NayaxConnectionStatus.PendingPermissions)]
    [InlineData(NayaxConnectionStatus.Ready)]
    [InlineData(NayaxConnectionStatus.NeedsAttention)]
    public async Task The_directory_reports_each_businesss_stored_connection_status(
        NayaxConnectionStatus stored)
    {
        await using var fixture = await DirectoryFixture.CreateAsync();
        await fixture.SeedNayaxConnectionAsync(fixture.TradingBusinessId, stored);

        var db = fixture.DeniedContext();
        var listed = await new EfPlatformBusinessDirectoryStore(db).ListAsync(CancellationToken.None);

        var trading = listed.Single(business => business.BusinessId == fixture.TradingBusinessId);
        Assert.Equal(stored, trading.NayaxConnectionStatus);
        Assert.True(trading.NayaxConfigured);

        Assert.All(
            listed.Where(business => business.BusinessId != fixture.TradingBusinessId),
            business =>
            {
                Assert.Equal(NayaxConnectionStatus.NotConfigured, business.NayaxConnectionStatus);
                Assert.False(business.NayaxConfigured);
            });

        // The ordinary read through the very same context still sees nothing, which is what keeps the
        // adapter's single IgnoreQueryFilters statement the whole of the exception.
        Assert.Empty(await db.BusinessNayaxConnections.ToListAsync());
    }

    /// <summary>
    /// Nothing tenant-owned is reachable through the context this adapter runs on. A product seeded
    /// for each business is invisible, and a tenant-owned write is refused outright - the same pair
    /// of assertions the invite-redemption adapter makes about its own denied scope.
    /// </summary>
    [Fact]
    public async Task Nothing_tenant_owned_is_readable_or_writable_through_the_same_context()
    {
        await using var fixture = await DirectoryFixture.CreateAsync();
        await fixture.SeedProductAsync(fixture.TradingBusinessId, "Trading product");
        await fixture.SeedProductAsync(fixture.PendingBusinessId, "Pending product");

        var db = fixture.DeniedContext();
        _ = await new EfPlatformBusinessDirectoryStore(db).ListAsync(CancellationToken.None);

        Assert.Empty(await db.Products.ToListAsync());
        Assert.Empty(await db.Categories.ToListAsync());

        db.Products.Add(new Product { Name = "Smuggled", BusinessId = fixture.TradingBusinessId });
        await Assert.ThrowsAsync<CrossBusinessAccessException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task The_pending_count_counts_only_the_applications_waiting_for_a_decision()
    {
        await using var fixture = await DirectoryFixture.CreateAsync();

        Assert.Equal(1, await fixture.Store().CountPendingAsync(CancellationToken.None));

        await fixture.SeedBusinessAsync("Inland Vending", BusinessStatus.Pending, appliedAtUtc: AppliedAt);

        Assert.Equal(2, await fixture.Store().CountPendingAsync(CancellationToken.None));
    }

    #endregion

    #region The decision read

    /// <summary>
    /// The applicant of a pending application is its one active membership, with the Entra pair the
    /// eligibility rule is re-checked for and the membership key the exclusion names.
    /// </summary>
    [Fact]
    public async Task The_decision_read_identifies_the_applicant_of_a_pending_application()
    {
        await using var fixture = await DirectoryFixture.CreateAsync();

        var target = await fixture.Store()
            .FindDecisionTargetAsync(fixture.PendingBusinessId, CancellationToken.None);

        Assert.NotNull(target);
        Assert.Equal(BusinessStatus.Pending, target!.Status);
        Assert.Equal("Coastal Vending", target.Name);
        Assert.Equal(fixture.PendingApplicantMembershipId, target.Applicant?.MembershipId);
        Assert.Equal(FirstOwnerObjectId, target.Applicant?.Identity?.ObjectId);
        Assert.True(target.Applicant?.IsActive);
    }

    /// <summary>
    /// A business with no single active membership has no identifiable applicant. Both directions
    /// matter: none, and more than one.
    /// </summary>
    [Fact]
    public async Task A_business_without_exactly_one_active_membership_has_no_identifiable_applicant()
    {
        await using var fixture = await DirectoryFixture.CreateAsync();

        Assert.Null(
            (await fixture.Store().FindDecisionTargetAsync(fixture.RejectedBusinessId, CancellationToken.None))
                !.Applicant);
        Assert.Null(
            (await fixture.Store().FindDecisionTargetAsync(fixture.TradingBusinessId, CancellationToken.None))
                !.Applicant);
    }

    [Fact]
    public async Task An_unknown_business_has_no_decision_target()
    {
        await using var fixture = await DirectoryFixture.CreateAsync();

        Assert.Null(await fixture.Store().FindDecisionTargetAsync(9999, CancellationToken.None));
    }

    #endregion

    #region The writes

    /// <summary>
    /// Approving writes the status and the decision instant, and nothing else anywhere: the
    /// applicant's membership, the other businesses and their members are all exactly as they were.
    /// </summary>
    [Fact]
    public async Task Approving_writes_only_the_status_and_the_decision_instant()
    {
        await using var fixture = await DirectoryFixture.CreateAsync();
        var before = await fixture.MembershipsAsync();

        await fixture.Store().ApproveAsync(fixture.PendingBusinessId, DecidedAt, CancellationToken.None);

        var business = await fixture.BusinessAsync(fixture.PendingBusinessId);
        Assert.Equal(BusinessStatus.Active, business.Status);
        Assert.Equal(DecidedAt, business.DecidedAtUtc);
        Assert.Equal(AppliedAt, business.AppliedAtUtc);
        Assert.Null(business.RejectionReason);

        Assert.Equal(
            before.Select(membership => (membership.Id, membership.IsActive, membership.StatusChangedAtUtc)),
            (await fixture.MembershipsAsync())
                .Select(membership => (membership.Id, membership.IsActive, membership.StatusChangedAtUtc)));
        Assert.Equal(
            BusinessStatus.Active,
            (await fixture.BusinessAsync(fixture.TradingBusinessId)).Status);
    }

    /// <summary>
    /// Rejecting writes the status, the reason and the instant, and releases the applicant's
    /// membership with that same instant as its state-change instant - which is what frees them to
    /// apply again.
    /// </summary>
    [Fact]
    public async Task Rejecting_records_the_reason_and_releases_the_applicants_membership()
    {
        await using var fixture = await DirectoryFixture.CreateAsync();

        await fixture.Store().RejectAsync(
            fixture.PendingBusinessId,
            "Needs a registered ABN.",
            DecidedAt,
            fixture.PendingApplicantMembershipId,
            CancellationToken.None);

        var business = await fixture.BusinessAsync(fixture.PendingBusinessId);
        Assert.Equal(BusinessStatus.Rejected, business.Status);
        Assert.Equal("Needs a registered ABN.", business.RejectionReason);
        Assert.Equal(DecidedAt, business.DecidedAtUtc);

        var membership = await fixture.MembershipAsync(fixture.PendingApplicantMembershipId);
        Assert.False(membership.IsActive);
        Assert.Equal(DecidedAt, membership.StatusChangedAtUtc);
        Assert.Equal(CreatedAt, membership.CreatedAtUtc);
    }

    /// <summary>
    /// A rejection that releases nothing leaves every membership row, and every row's state-change
    /// instant, exactly as it was.
    /// </summary>
    [Fact]
    public async Task Rejecting_without_a_membership_to_release_touches_no_membership()
    {
        await using var fixture = await DirectoryFixture.CreateAsync();
        var before = await fixture.MembershipsAsync();

        await fixture.Store().RejectAsync(
            fixture.PendingBusinessId,
            reason: null,
            DecidedAt,
            deactivateMembershipId: null,
            CancellationToken.None);

        Assert.Equal(BusinessStatus.Rejected, (await fixture.BusinessAsync(fixture.PendingBusinessId)).Status);
        Assert.Null((await fixture.BusinessAsync(fixture.PendingBusinessId)).RejectionReason);
        Assert.Equal(
            before.Select(membership => (membership.Id, membership.IsActive, membership.StatusChangedAtUtc)),
            (await fixture.MembershipsAsync())
                .Select(membership => (membership.Id, membership.IsActive, membership.StatusChangedAtUtc)));
    }

    /// <summary>
    /// A membership key is only ever released together with the business it belongs to. Another
    /// business's member cannot be revoked by a decision on this one, even if the key were named:
    /// the statement pairs the two, finds nothing, and writes nothing at all - not even the
    /// business's own status.
    /// </summary>
    [Fact]
    public async Task A_membership_of_another_business_cannot_be_released_by_this_decision()
    {
        await using var fixture = await DirectoryFixture.CreateAsync();

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => fixture.Store().RejectAsync(
            fixture.PendingBusinessId,
            "No.",
            DecidedAt,
            fixture.TradingOwnerMembershipId,
            CancellationToken.None));

        Assert.Equal(BusinessStatus.Pending, (await fixture.BusinessAsync(fixture.PendingBusinessId)).Status);
        Assert.True((await fixture.MembershipAsync(fixture.TradingOwnerMembershipId)).IsActive);
    }

    #endregion

    #region The lifecycle read and writes (issue #545)

    /// <summary>
    /// The lifecycle read reports the business's status and <em>every</em> membership it holds -
    /// active and revoked alike, with the role exactly as stored and the display-only name - because
    /// two rules read them: whether an active Owner is left, and whether this business holds the
    /// membership a release names.
    /// </summary>
    [Fact]
    public async Task The_lifecycle_read_reports_the_status_and_every_membership()
    {
        await using var fixture = await DirectoryFixture.CreateAsync();
        var named = await fixture.SeedMembershipAsync(
            fixture.TradingBusinessId,
            "77777777-7777-7777-7777-777777777777",
            BusinessRole.Operator,
            isActive: false,
            displayName: "Dana Operator");

        var target = await fixture.Store()
            .FindLifecycleTargetAsync(fixture.TradingBusinessId, CancellationToken.None);

        Assert.NotNull(target);
        Assert.Equal(fixture.TradingBusinessId, target!.BusinessId);
        Assert.Equal("Vending Co", target.Name);
        Assert.Equal(BusinessStatus.Active, target.Status);

        Assert.Equal(4, target.Members.Count);
        var owner = target.Members.Single(member => member.MembershipId == fixture.TradingOwnerMembershipId);
        Assert.Equal(BusinessRole.Owner, owner.StoredRole);
        Assert.True(owner.IsActive);
        Assert.Null(owner.DisplayName);

        var revoked = target.Members.Single(member => member.MembershipId == named);
        Assert.Equal("Dana Operator", revoked.DisplayName);
        Assert.False(revoked.IsActive);

        // Only this business's memberships: the applicant of the pending application beside it is
        // not reachable through a read of this one.
        Assert.DoesNotContain(
            fixture.PendingApplicantMembershipId,
            target.Members.Select(member => member.MembershipId));
    }

    [Fact]
    public async Task An_unknown_business_has_no_lifecycle_target()
    {
        await using var fixture = await DirectoryFixture.CreateAsync();

        Assert.Null(await fixture.Store().FindLifecycleTargetAsync(9999, CancellationToken.None));
    }

    /// <summary>
    /// Deactivating writes the status column and nothing else anywhere: the application instants and
    /// the rejection reason are untouched - a deactivation is not a decision on an application - and
    /// so is every membership row, flag and state-change instant alike.
    /// </summary>
    [Fact]
    public async Task Deactivating_writes_only_the_status()
    {
        await using var fixture = await DirectoryFixture.CreateAsync();
        var before = await fixture.MembershipsAsync();

        await fixture.Store().DeactivateAsync(fixture.TradingBusinessId, CancellationToken.None);

        var business = await fixture.BusinessAsync(fixture.TradingBusinessId);
        Assert.Equal(BusinessStatus.Deactivated, business.Status);
        Assert.Null(business.DecidedAtUtc);
        Assert.Null(business.AppliedAtUtc);
        Assert.Null(business.RejectionReason);
        Assert.Equal("Vending Co", business.Name);

        AssertMembershipsUnchanged(before, await fixture.MembershipsAsync());
        Assert.Equal(
            BusinessStatus.Pending,
            (await fixture.BusinessAsync(fixture.PendingBusinessId)).Status);
    }

    /// <summary>
    /// Reactivating writes the status column and nothing else either - in particular it restores no
    /// membership: a release or a revocation is a deliberate decision about a person, and a business
    /// returning is not a reason to undo one.
    /// </summary>
    [Fact]
    public async Task Reactivating_writes_only_the_status_and_restores_no_membership()
    {
        await using var fixture = await DirectoryFixture.CreateAsync();
        await fixture.Store().DeactivateAsync(fixture.TradingBusinessId, CancellationToken.None);
        await fixture.Store().ReleaseMemberAsync(
            fixture.TradingBusinessId,
            fixture.TradingOwnerMembershipId,
            DecidedAt,
            CancellationToken.None);
        var before = await fixture.MembershipsAsync();

        await fixture.Store().ReactivateAsync(fixture.TradingBusinessId, CancellationToken.None);

        Assert.Equal(
            BusinessStatus.Active,
            (await fixture.BusinessAsync(fixture.TradingBusinessId)).Status);
        AssertMembershipsUnchanged(before, await fixture.MembershipsAsync());
        Assert.False((await fixture.MembershipAsync(fixture.TradingOwnerMembershipId)).IsActive);
    }

    /// <summary>
    /// Releasing a member writes that one membership's state together with the instant it changed,
    /// and leaves its creation instant, its role and every other row alone.
    /// </summary>
    [Fact]
    public async Task Releasing_a_member_writes_the_state_and_the_instant_together()
    {
        await using var fixture = await DirectoryFixture.CreateAsync();

        await fixture.Store().ReleaseMemberAsync(
            fixture.TradingBusinessId,
            fixture.TradingOwnerMembershipId,
            DecidedAt,
            CancellationToken.None);

        var membership = await fixture.MembershipAsync(fixture.TradingOwnerMembershipId);
        Assert.False(membership.IsActive);
        Assert.Equal(DecidedAt, membership.StatusChangedAtUtc);
        Assert.Equal(CreatedAt, membership.CreatedAtUtc);
        Assert.Equal(BusinessRole.Owner, membership.Role);

        // The business itself is untouched: its status is somebody else's write.
        Assert.Equal(
            BusinessStatus.Active,
            (await fixture.BusinessAsync(fixture.TradingBusinessId)).Status);
        Assert.True((await fixture.MembershipAsync(fixture.PendingApplicantMembershipId)).IsActive);
    }

    /// <summary>
    /// A membership key is only ever released together with the business it belongs to. Naming
    /// another business's membership on this business's release finds nothing and writes nothing -
    /// the statement pairs the two.
    /// </summary>
    [Fact]
    public async Task A_membership_of_another_business_cannot_be_released()
    {
        await using var fixture = await DirectoryFixture.CreateAsync();

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => fixture.Store().ReleaseMemberAsync(
            fixture.TradingBusinessId,
            fixture.PendingApplicantMembershipId,
            DecidedAt,
            CancellationToken.None));

        var membership = await fixture.MembershipAsync(fixture.PendingApplicantMembershipId);
        Assert.True(membership.IsActive);
        Assert.Equal(CreatedAt, membership.StatusChangedAtUtc);
    }

    private static void AssertMembershipsUnchanged(
        IReadOnlyList<BusinessMembership> before,
        IReadOnlyList<BusinessMembership> after) =>
        Assert.Equal(
            before.Select(membership =>
                (membership.Id, membership.IsActive, membership.Role, membership.StatusChangedAtUtc)),
            after.Select(membership =>
                (membership.Id, membership.IsActive, membership.Role, membership.StatusChangedAtUtc)));

    #endregion

    #region The fixture

    /// <summary>
    /// A migrated SQLite database with three businesses: one pending application with its single
    /// applicant, one trading business with two active members and one revoked one, and one rejected
    /// application whose applicant was already released.
    /// </summary>
    private sealed class DirectoryFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AppDbContext> _options;
        private readonly List<AppDbContext> _contexts = [];

        private DirectoryFixture(SqliteConnection connection, DbContextOptions<AppDbContext> options)
        {
            _connection = connection;
            _options = options;
        }

        public int PendingBusinessId { get; private set; }

        public int TradingBusinessId { get; private set; }

        public int RejectedBusinessId { get; private set; }

        public int PendingApplicantMembershipId { get; private set; }

        public int TradingOwnerMembershipId { get; private set; }

        public static async Task<DirectoryFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var fixture = new DirectoryFixture(
                connection,
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);

            await using var setup = TestAppDbContext.Unrestricted(fixture._options);
            await setup.Database.MigrateAsync();

            fixture.TradingBusinessId = await fixture.SeedBusinessAsync("Vending Co", BusinessStatus.Active);
            fixture.PendingBusinessId = await fixture.SeedBusinessAsync(
                "Coastal Vending",
                BusinessStatus.Pending,
                contactEmail: "owner@coastal.example.com",
                abn: "51824753556",
                appliedAtUtc: AppliedAt);
            fixture.RejectedBusinessId = await fixture.SeedBusinessAsync(
                "Inland Vending",
                BusinessStatus.Rejected,
                appliedAtUtc: AppliedAt,
                decidedAtUtc: DecidedAt,
                rejectionReason: "Needs a registered ABN.");

            fixture.TradingOwnerMembershipId = await fixture.SeedMembershipAsync(
                fixture.TradingBusinessId,
                SecondOwnerObjectId,
                BusinessRole.Owner,
                isActive: true);
            await fixture.SeedMembershipAsync(
                fixture.TradingBusinessId,
                ManagerObjectId,
                BusinessRole.Manager,
                isActive: true);
            await fixture.SeedMembershipAsync(
                fixture.TradingBusinessId,
                "55555555-5555-5555-5555-555555555555",
                BusinessRole.Operator,
                isActive: false);

            fixture.PendingApplicantMembershipId = await fixture.SeedMembershipAsync(
                fixture.PendingBusinessId,
                FirstOwnerObjectId,
                BusinessRole.Owner,
                isActive: true);

            // The rejected application's applicant was released by the rejection, which is why the
            // business has no active membership at all.
            await fixture.SeedMembershipAsync(
                fixture.RejectedBusinessId,
                "66666666-6666-6666-6666-666666666666",
                BusinessRole.Owner,
                isActive: false);

            return fixture;
        }

        /// <summary>
        /// The adapter over a fresh denied-scope context - the scope a platform administrator's
        /// request really carries, because nothing resolves a business for somebody with no
        /// membership anywhere.
        /// </summary>
        public EfPlatformBusinessDirectoryStore Store() => new(DeniedContext());

        public AppDbContext DeniedContext()
        {
            var context = TestAppDbContext.Denied(_options);
            _contexts.Add(context);
            return context;
        }

        public async Task<int> SeedBusinessAsync(
            string name,
            BusinessStatus status,
            string? contactEmail = null,
            string? abn = null,
            DateTime? appliedAtUtc = null,
            DateTime? decidedAtUtc = null,
            string? rejectionReason = null)
        {
            await using var db = TestAppDbContext.Unrestricted(_options);
            var business = new Business
            {
                Name = name,
                TimeZoneId = "Australia/Sydney",
                Status = status,
                ContactEmail = contactEmail,
                Abn = abn,
                AppliedAtUtc = appliedAtUtc,
                DecidedAtUtc = decidedAtUtc,
                RejectionReason = rejectionReason,
                CreatedAtUtc = CreatedAt,
            };

            db.Businesses.Add(business);
            await db.SaveChangesAsync();
            return business.Id;
        }

        public async Task<int> SeedMembershipAsync(
            int businessId,
            string objectId,
            BusinessRole role,
            bool isActive,
            string? displayName = null)
        {
            await using var db = TestAppDbContext.Unrestricted(_options);
            var membership = new BusinessMembership
            {
                BusinessId = businessId,
                DirectoryTenantId = DirectoryTenantId,
                ObjectId = objectId,
                Role = role,
                IsActive = isActive,
                DisplayName = displayName,
                CreatedAtUtc = CreatedAt,
                StatusChangedAtUtc = CreatedAt,
            };

            db.BusinessMemberships.Add(membership);
            await db.SaveChangesAsync();
            return membership.Id;
        }

        /// <summary>
        /// One stored Nayax connection, exactly as a save leaves it: a ciphertext that is not the
        /// token, so a test can also assert that no part of it reaches the directory. The status is
        /// the fixture's one parameter, because what the directory reports about a connection is that
        /// stored value and nothing else (issue #983).
        /// </summary>
        public async Task SeedNayaxConnectionAsync(
            int businessId,
            NayaxConnectionStatus status = NayaxConnectionStatus.Ready)
        {
            await using var db = TestAppDbContext.Unrestricted(_options);
            db.BusinessNayaxConnections.Add(new BusinessNayaxConnection
            {
                BusinessId = businessId,
                OperatorId = "2002736764",
                AccessTokenCiphertext = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"cipher:{Token}")),
                EncryptionKeyId = "2026-10",
                Status = status,
                CredentialRevision = 1,
                LastTestedAtUtc = LastTestedAt,
                UpdatedAtUtc = CreatedAt,
            });
            await db.SaveChangesAsync();
        }

        public async Task SeedProductAsync(int businessId, string name)
        {
            await using var db = TestAppDbContext.Unrestricted(_options);
            db.Products.Add(new Product { BusinessId = businessId, Name = name });
            await db.SaveChangesAsync();
        }

        public async Task<Business> BusinessAsync(int businessId)
        {
            await using var db = TestAppDbContext.Unrestricted(_options);
            return await db.Businesses.AsNoTracking().SingleAsync(business => business.Id == businessId);
        }

        public async Task<BusinessMembership> MembershipAsync(int membershipId)
        {
            await using var db = TestAppDbContext.Unrestricted(_options);
            return await db.BusinessMemberships
                .AsNoTracking()
                .SingleAsync(membership => membership.Id == membershipId);
        }

        public async Task<IReadOnlyList<BusinessMembership>> MembershipsAsync()
        {
            await using var db = TestAppDbContext.Unrestricted(_options);
            return await db.BusinessMemberships
                .AsNoTracking()
                .OrderBy(membership => membership.Id)
                .ToListAsync();
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var context in _contexts)
            {
                await context.DisposeAsync();
            }

            await _connection.DisposeAsync();
        }
    }

    #endregion
}
