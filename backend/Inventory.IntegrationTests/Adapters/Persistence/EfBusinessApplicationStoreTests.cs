using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The business-application adapter of issue #541 against relational SQLite, with the real migration
/// history.
///
/// <para>Every test here runs the adapter over a <strong>denied</strong> context, because that is
/// the only scope an application request ever carries: the caller has no membership, so nothing
/// resolved a business for them - and in this case there is no business to resolve yet. Proving the
/// adapter works under that scope is proving the ownership decision, and proving it still reads and
/// writes nothing tenant-owned is proving that the exemption is about these two tables and nothing
/// else.</para>
///
/// <para>One existing business is seeded throughout, so the assertions that matter most about a new
/// tenant arriving can be made: a creation leaves it exactly as it was, and the read stops at the
/// business it was given.</para>
/// </summary>
public class EfBusinessApplicationStoreTests
{
    private const int ExistingBusinessId = 1;
    private const string ValidAbn = "51824753556";

    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string ApplicantOid = "22222222-2222-2222-2222-222222222222";

    private static readonly DateTime CreatedAt = new(2026, 10, 12, 1, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime AppliedAt = CreatedAt.AddDays(1);

    #region Creating the application

    /// <summary>
    /// The creation: a Pending business holding exactly the validated settings, with no decision
    /// recorded, plus the applicant's own active Owner membership - both written under the denied
    /// scope an application request carries.
    /// </summary>
    [Fact]
    public async Task It_creates_a_pending_business_and_the_applicants_own_owner_membership()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Denied(options);

            var created = await new EfBusinessApplicationStore(db).CreateAsync(
                Settings(),
                Actor(),
                MemberProfile.Create("Dana Doe", "dana@example.test"),
                AppliedAt,
                CancellationToken.None);

            Assert.Equal(BusinessStatus.Pending, created.Status);
            Assert.Equal(AppliedAt, created.AppliedAtUtc);
            Assert.NotEqual(ExistingBusinessId, created.BusinessId.Value);

            var business = await ReadBusinessAsync(options, created.BusinessId.Value);
            Assert.Equal("Coastal Vending", business.Name);
            Assert.Equal("owner@coastal.example.com", business.ContactEmail);
            Assert.Equal(ValidAbn, business.Abn);
            Assert.True(business.GstRegistered);
            Assert.Equal("Australia/Brisbane", business.TimeZoneId);
            Assert.Equal(BusinessStatus.Pending, business.Status);
            Assert.Equal(AppliedAt, business.AppliedAtUtc);
            Assert.Equal(AppliedAt, business.CreatedAtUtc);

            // Nobody has decided anything, so neither column is written.
            Assert.Null(business.DecidedAtUtc);
            Assert.Null(business.RejectionReason);

            var membership = Assert.Single(await ReadMembershipsAsync(options, created.BusinessId.Value));
            Assert.Equal(Actor().DirectoryTenantId, membership.DirectoryTenantId);
            Assert.Equal(Actor().ObjectId, membership.ObjectId);
            Assert.Equal(BusinessRole.Owner, membership.Role);
            Assert.True(membership.IsActive);
            Assert.Equal("Dana Doe", membership.DisplayName);
            Assert.Equal("dana@example.test", membership.Email);

            // One instant for the creation and the state change alike (issue #522).
            Assert.Equal(AppliedAt, membership.CreatedAtUtc);
            Assert.Equal(AppliedAt, membership.StatusChangedAtUtc);
        }
    }

    /// <summary>
    /// A token that reported neither display claim leaves both columns empty rather than storing a
    /// placeholder (issue #524).
    /// </summary>
    [Fact]
    public async Task A_token_with_no_display_claims_stores_nothing_for_them()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Denied(options);

            var created = await new EfBusinessApplicationStore(db).CreateAsync(
                Settings(),
                Actor(),
                MemberProfile.None,
                AppliedAt,
                CancellationToken.None);

            var membership = Assert.Single(await ReadMembershipsAsync(options, created.BusinessId.Value));
            Assert.Null(membership.DisplayName);
            Assert.Null(membership.Email);
        }
    }

    /// <summary>
    /// An application with no ABN stores none - <c>null</c>, its single representation of "not
    /// recorded" - and still records the GST registration it stated.
    /// </summary>
    [Fact]
    public async Task An_application_without_an_abn_stores_none()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Denied(options);

            var created = await new EfBusinessApplicationStore(db).CreateAsync(
                Settings() with { Abn = null, GstRegistered = false },
                Actor(),
                MemberProfile.None,
                AppliedAt,
                CancellationToken.None);

            var business = await ReadBusinessAsync(options, created.BusinessId.Value);
            Assert.Null(business.Abn);
            Assert.False(business.GstRegistered);
        }
    }

    /// <summary>
    /// The business starts empty: nothing creates a Nayax connection record for it, so its status is
    /// <c>NotConfigured</c> by virtue of nothing being stored, and it holds no machine, product,
    /// purchase or sale either.
    /// </summary>
    [Fact]
    public async Task The_new_business_starts_with_no_nayax_connection_and_no_data()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Denied(options);

            var created = await new EfBusinessApplicationStore(db).CreateAsync(
                Settings(),
                Actor(),
                MemberProfile.None,
                AppliedAt,
                CancellationToken.None);

            await using var verify = TestAppDbContext.Unrestricted(options);
            var id = created.BusinessId.Value;

            Assert.Empty(await verify.BusinessNayaxConnections.Where(row => row.BusinessId == id).ToListAsync());
            Assert.Empty(await verify.Products.Where(row => row.BusinessId == id).ToListAsync());
            Assert.Empty(await verify.Receipts.Where(row => row.BusinessId == id).ToListAsync());
            Assert.Empty(await verify.NayaxSales.Where(row => row.BusinessId == id).ToListAsync());
            Assert.Empty(await verify.MemberInvites.Where(row => row.BusinessId == id).ToListAsync());
        }
    }

    /// <summary>
    /// The existing business is untouched by a new tenant arriving: its row, its status and its
    /// memberships are exactly as they were.
    /// </summary>
    [Fact]
    public async Task Creating_an_application_leaves_the_existing_business_exactly_as_it_was()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await SeedMembershipAsync(options, ExistingBusinessId, "33333333-3333-3333-3333-333333333333");

            await using var db = TestAppDbContext.Denied(options);
            await new EfBusinessApplicationStore(db).CreateAsync(
                Settings(),
                Actor(),
                MemberProfile.None,
                AppliedAt,
                CancellationToken.None);

            var existing = await ReadBusinessAsync(options, ExistingBusinessId);
            Assert.Equal("Vending Co", existing.Name);
            Assert.Equal(BusinessStatus.Active, existing.Status);
            Assert.Equal(CreatedAt, existing.CreatedAtUtc);
            Assert.Null(existing.AppliedAtUtc);

            var membership = Assert.Single(await ReadMembershipsAsync(options, ExistingBusinessId));
            Assert.Equal("33333333-3333-3333-3333-333333333333", membership.ObjectId);
            Assert.Equal(CreatedAt, membership.StatusChangedAtUtc);
        }
    }

    /// <summary>
    /// The filtered one-active-membership index still applies to this write path: an applicant who
    /// already holds an active membership somewhere is refused by the database even if the rules
    /// above it were somehow bypassed, and neither the business nor the membership is stored.
    /// </summary>
    [Fact]
    public async Task The_index_refuses_an_applicant_who_already_holds_an_active_membership()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await SeedMembershipAsync(options, ExistingBusinessId, ApplicantOid);

            await using var db = TestAppDbContext.Denied(options);

            await Assert.ThrowsAnyAsync<DbUpdateException>(() => new EfBusinessApplicationStore(db).CreateAsync(
                Settings(),
                Actor(),
                MemberProfile.None,
                AppliedAt,
                CancellationToken.None));

            await using var verify = TestAppDbContext.Unrestricted(options);
            Assert.Single(await verify.Businesses.ToListAsync());
            Assert.Single(await verify.BusinessMemberships.ToListAsync());
        }
    }

    /// <summary>
    /// The central business enforcers run on this write, because it goes through
    /// <c>SaveChanges</c> on tracked entities rather than a set-based insert: a time zone the host
    /// cannot resolve, and an ABN that is not a well-formed one, are both refused here and store
    /// nothing. That is the reason the adapter is written the way it is.
    /// </summary>
    [Theory]
    [InlineData("Not/A/Zone", ValidAbn)]
    [InlineData("Australia/Brisbane", "51824753557")]
    public async Task The_central_business_enforcers_refuse_an_unusable_application(
        string timeZoneId,
        string abn)
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Denied(options);

            await Assert.ThrowsAsync<DomainValidationException>(() =>
                new EfBusinessApplicationStore(db).CreateAsync(
                    Settings() with { TimeZoneId = timeZoneId, Abn = abn },
                    Actor(),
                    MemberProfile.None,
                    AppliedAt,
                    CancellationToken.None));

            await using var verify = TestAppDbContext.Unrestricted(options);
            Assert.Single(await verify.Businesses.ToListAsync());
            Assert.Empty(await verify.BusinessMemberships.ToListAsync());
        }
    }

    #endregion

    #region Reading the application

    /// <summary>
    /// The read a resubmission is compared against: the business the applicant's own membership
    /// named, with its stored settings, status and application instant, under the denied scope.
    /// </summary>
    [Fact]
    public async Task It_reads_the_application_a_business_records()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            BusinessId pendingId;
            await using (var writer = TestAppDbContext.Denied(options))
            {
                pendingId = (await new EfBusinessApplicationStore(writer).CreateAsync(
                        Settings(),
                        Actor(),
                        MemberProfile.None,
                        AppliedAt,
                        CancellationToken.None))
                    .BusinessId;
            }

            await using var db = TestAppDbContext.Denied(options);

            var found = await new EfBusinessApplicationStore(db).FindAsync(pendingId, CancellationToken.None);

            Assert.NotNull(found);
            Assert.Equal(pendingId, found!.BusinessId);
            Assert.Equal(BusinessStatus.Pending, found.Status);
            Assert.Equal(AppliedAt, found.AppliedAtUtc);
            Assert.Equal(Settings(), found.Settings);
        }
    }

    /// <summary>
    /// A business that never went through an application reports no application instant rather than
    /// its creation one: the existing business predates the form, and inventing an instant would
    /// claim a business event that never happened (issue #540).
    /// </summary>
    [Fact]
    public async Task A_business_that_never_applied_reports_no_application_instant()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Denied(options);

            var found = await new EfBusinessApplicationStore(db)
                .FindAsync(BusinessId.From(ExistingBusinessId), CancellationToken.None);

            Assert.NotNull(found);
            Assert.Null(found!.AppliedAtUtc);
            Assert.Equal(BusinessStatus.Active, found.Status);
            Assert.Null(found.Settings.ContactEmail);
            Assert.Null(found.Settings.Abn);
            Assert.Null(found.Settings.GstRegistered);
        }
    }

    [Fact]
    public async Task A_business_that_does_not_exist_is_not_found()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Denied(options);

            Assert.Null(await new EfBusinessApplicationStore(db)
                .FindAsync(BusinessId.From(9999), CancellationToken.None));
        }
    }

    /// <summary>
    /// The read reports a status this code does not declare exactly as stored, so the caller refuses
    /// it rather than treating it as a pending application.
    /// </summary>
    [Fact]
    public async Task An_undeclared_stored_status_is_reported_exactly_as_stored()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using (var seed = TestAppDbContext.Unrestricted(options))
            {
                // Written round the enforcers on purpose: only a row somebody else wrote can hold a
                // status this deployment does not declare.
                await seed.Database.ExecuteSqlRawAsync(
                    "UPDATE Businesses SET Status = 77 WHERE Id = {0}",
                    ExistingBusinessId);
            }

            await using var db = TestAppDbContext.Denied(options);

            var found = await new EfBusinessApplicationStore(db)
                .FindAsync(BusinessId.From(ExistingBusinessId), CancellationToken.None);

            Assert.NotNull(found);
            Assert.Equal((BusinessStatus)77, found!.Status);
        }
    }

    #endregion

    #region The denied scope stays denied

    /// <summary>
    /// The context an application runs on reads and writes no business data at all. It is the whole
    /// reason the exemption is safe: the two tables this adapter reaches are structurally
    /// non-tenant-owned, and everything else stays behind the filters and the ownership
    /// enforcement.
    /// </summary>
    [Fact]
    public async Task The_denied_context_still_reads_and_writes_no_business_data()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using (var seed = TestAppDbContext.For(options, ExistingBusinessId))
            {
                seed.Categories.Add(new Category { Name = "Snacks" });
                await seed.SaveChangesAsync();
            }

            await using var db = TestAppDbContext.Denied(options);

            await new EfBusinessApplicationStore(db).CreateAsync(
                Settings(),
                Actor(),
                MemberProfile.None,
                AppliedAt,
                CancellationToken.None);

            Assert.Empty(await db.Categories.AsNoTracking().ToListAsync());

            db.Categories.Add(new Category { Name = "Smuggled in" });
            await Assert.ThrowsAsync<CrossBusinessAccessException>(() => db.SaveChangesAsync());
        }
    }

    #endregion

    private static BusinessSettings Settings() =>
        new("Coastal Vending", "owner@coastal.example.com", ValidAbn, true, "Australia/Brisbane");

    private static ActorIdentity Actor(string objectId = ApplicantOid)
    {
        Assert.True(ActorIdentity.TryCreate(Tid, objectId, out var actor));
        return actor!;
    }

    private static async Task<(SqliteConnection Connection, DbContextOptions<AppDbContext> Options)> CreateSqliteAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var setup = TestAppDbContext.Unrestricted(options);
        await setup.Database.MigrateAsync();
        setup.Businesses.Add(new Business
        {
            Id = ExistingBusinessId,
            Name = "Vending Co",
            TimeZoneId = "Australia/Sydney",
            Status = BusinessStatus.Active,
            CreatedAtUtc = CreatedAt,
        });
        await setup.SaveChangesAsync();
        return (connection, options);
    }

    private static async Task SeedMembershipAsync(
        DbContextOptions<AppDbContext> options,
        int businessId,
        string objectId)
    {
        await using var db = TestAppDbContext.Unrestricted(options);
        db.BusinessMemberships.Add(new BusinessMembership
        {
            BusinessId = businessId,
            DirectoryTenantId = Tid,
            ObjectId = objectId,
            Role = BusinessRole.Owner,
            IsActive = true,
            CreatedAtUtc = CreatedAt,
            StatusChangedAtUtc = CreatedAt,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<Business> ReadBusinessAsync(DbContextOptions<AppDbContext> options, int businessId)
    {
        await using var db = TestAppDbContext.Unrestricted(options);
        return await db.Businesses.AsNoTracking().SingleAsync(business => business.Id == businessId);
    }

    private static async Task<List<BusinessMembership>> ReadMembershipsAsync(
        DbContextOptions<AppDbContext> options,
        int businessId)
    {
        await using var db = TestAppDbContext.Unrestricted(options);
        return await db.BusinessMemberships
            .AsNoTracking()
            .Where(membership => membership.BusinessId == businessId)
            .OrderBy(membership => membership.Id)
            .ToListAsync();
    }
}
