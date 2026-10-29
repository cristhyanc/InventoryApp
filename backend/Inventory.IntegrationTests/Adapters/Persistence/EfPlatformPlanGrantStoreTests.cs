using Inventory.Application.PlatformPlans;
using Inventory.Domain.Subscriptions;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The manual plan grant adapter of issue #982 against relational SQLite with the real migration
/// history: what a platform decision may read and write under a <em>denied</em> business scope, and
/// everything the same context still refuses.
///
/// <para>Relational rather than a model double, for the reason the other store tests in this folder
/// are: the write lease, the central ownership stamp, the tenant query filters and "the grant and its
/// audit row commit together or not at all" are properties of a real database and a real
/// <c>SaveChanges</c>, not of a fake.</para>
///
/// <para><strong>Two businesses throughout</strong> (AGENTS.md § Tenant ownership and data
/// isolation): every decision names one of them, so a grant reaching the other would fail a test
/// rather than be invisible.</para>
/// </summary>
public class EfPlatformPlanGrantStoreTests : IAsyncLifetime
{
    private const int FirstBusinessId = 1;
    private const int SecondBusinessId = 2;
    private const string DirectoryTenantId = "99999999-9999-9999-9999-999999999999";
    private const string AdministratorObjectId = "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA";

    private static readonly DateTime Now = new(2026, 10, 29, 13, 45, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private DbContextOptions<AppDbContext> _options = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        await using var db = TestAppDbContext.Unrestricted(_options);
        await db.Database.MigrateAsync();

        db.Businesses.AddRange(
            NewBusiness(FirstBusinessId, "Vending Co"),
            NewBusiness(SecondBusinessId, "Second Vending Co"));
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task An_unknown_business_is_not_found()
    {
        await using var db = TestAppDbContext.Denied(_options);

        Assert.Null(await new EfPlatformPlanGrantStore(db).FindAsync(404, CancellationToken.None));
    }

    [Fact]
    public async Task A_business_with_no_complimentary_grant_reads_as_holding_none()
    {
        await using var db = TestAppDbContext.Denied(_options);

        var target = await new EfPlatformPlanGrantStore(db).FindAsync(FirstBusinessId, CancellationToken.None);

        Assert.NotNull(target);
        Assert.Equal(FirstBusinessId, target.BusinessId);
        Assert.Equal("Vending Co", target.Name);
        Assert.Empty(target.Grants);
    }

    /// <summary>
    /// The grandfathered grant of issue #966 is invisible to this surface: every statement filters on
    /// the complimentary source, so the row that command wrote cannot be read, compared against or
    /// written through here.
    /// </summary>
    [Fact]
    public async Task The_grandfathered_grant_is_not_read_by_this_surface()
    {
        await SeedGrantAsync(FirstBusinessId, SubscriptionPlan.Pro, ManualPlanGrantSource.Grandfathered);

        await using var db = TestAppDbContext.Denied(_options);
        var target = await new EfPlatformPlanGrantStore(db).FindAsync(FirstBusinessId, CancellationToken.None);

        Assert.Empty(target!.Grants);
    }

    /// <summary>
    /// The write, end to end on one context: the grant row and its audit row arrive together, the
    /// business key is stamped by the central enforcer rather than assigned by the adapter, and the
    /// instants come back marked UTC.
    /// </summary>
    [Fact]
    public async Task A_first_decision_writes_the_grant_and_its_audit_row_together()
    {
        await using (var db = TestAppDbContext.Denied(_options))
        {
            var store = new EfPlatformPlanGrantStore(db);
            await using var transaction = await store.BeginWriteTransactionAsync(CancellationToken.None);

            await store.SaveAsync(Write(FirstBusinessId, SubscriptionPlan.Pro), CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        await using var reader = TestAppDbContext.Unrestricted(_options);

        var grant = await reader.BusinessPlanGrants.AsNoTracking().SingleAsync();
        Assert.Equal(FirstBusinessId, grant.BusinessId);
        Assert.Equal(SubscriptionPlan.Pro, grant.Plan);
        Assert.Equal(ManualPlanGrantSource.Complimentary, grant.Source);
        Assert.Equal(Now, grant.StartsAtUtc);
        Assert.Equal(DateTimeKind.Utc, grant.StartsAtUtc.Kind);
        Assert.Null(grant.EndsAtUtc);
        Assert.Equal("Complimentary pilot", grant.Reason);
        Assert.Equal(1, grant.Revision);

        var audit = await reader.BusinessPlanGrantAudits.AsNoTracking().SingleAsync();
        Assert.Equal(FirstBusinessId, audit.BusinessId);
        Assert.Equal(PlatformPlanGrantChange.Created, audit.Change);
        Assert.Null(audit.FromPlan);
        Assert.Equal(SubscriptionPlan.Pro, audit.ToPlan);
        Assert.Equal("Complimentary pilot", audit.Reason);
        Assert.Equal(1, audit.Revision);
        Assert.Equal(DirectoryTenantId, audit.ActorDirectoryTenantId);
        Assert.Equal(AdministratorObjectId, audit.ActorObjectId);
        Assert.Equal(Now, audit.OccurredAtUtc);
        Assert.Equal(DateTimeKind.Utc, audit.OccurredAtUtc.Kind);

        // And nothing else was written: no billing subscription, and no grandfathered grant.
        Assert.Empty(await reader.BusinessBillingSubscriptions.AsNoTracking().ToListAsync());
        Assert.Empty(await reader.BusinessPlanGrants
            .AsNoTracking()
            .Where(row => row.Source == ManualPlanGrantSource.Grandfathered)
            .ToListAsync());
    }

    /// <summary>
    /// The criterion "committed together or not at all", read from the other direction: a transaction
    /// that is never committed leaves neither row behind.
    /// </summary>
    [Fact]
    public async Task A_decision_that_is_not_committed_leaves_neither_row()
    {
        await using (var db = TestAppDbContext.Denied(_options))
        {
            var store = new EfPlatformPlanGrantStore(db);
            await using var transaction = await store.BeginWriteTransactionAsync(CancellationToken.None);

            await store.SaveAsync(Write(FirstBusinessId, SubscriptionPlan.Pro), CancellationToken.None);

            // Disposed without committing, which is what a refused rule does to a decision.
        }

        await using var reader = TestAppDbContext.Unrestricted(_options);
        Assert.Empty(await reader.BusinessPlanGrants.AsNoTracking().ToListAsync());
        Assert.Empty(await reader.BusinessPlanGrantAudits.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// A second decision updates the one grant row and appends a second audit row: the trail grows,
    /// the grant does not multiply, and the grandfathered row is untouched.
    /// </summary>
    [Fact]
    public async Task A_second_decision_updates_the_grant_and_appends_to_the_trail()
    {
        await SeedGrantAsync(FirstBusinessId, SubscriptionPlan.Pro, ManualPlanGrantSource.Grandfathered);

        await WriteAsync(Write(FirstBusinessId, SubscriptionPlan.Starter));
        await WriteAsync(Write(FirstBusinessId, SubscriptionPlan.Pro) with
        {
            Revision = 2,
            Change = PlatformPlanGrantChange.Changed,
            FromPlan = SubscriptionPlan.Starter,
            AuditReason = "Upgraded for the pilot",
        });

        await using var reader = TestAppDbContext.Unrestricted(_options);

        var complimentary = await reader.BusinessPlanGrants
            .AsNoTracking()
            .Where(row => row.Source == ManualPlanGrantSource.Complimentary)
            .SingleAsync();
        Assert.Equal(SubscriptionPlan.Pro, complimentary.Plan);
        Assert.Equal(2, complimentary.Revision);

        var grandfathered = await reader.BusinessPlanGrants
            .AsNoTracking()
            .Where(row => row.Source == ManualPlanGrantSource.Grandfathered)
            .SingleAsync();
        Assert.Equal(SubscriptionPlan.Pro, grandfathered.Plan);
        Assert.Equal(0, grandfathered.Revision);

        var trail = await reader.BusinessPlanGrantAudits
            .AsNoTracking()
            .OrderBy(row => row.Id)
            .ToListAsync();
        Assert.Equal(2, trail.Count);
        Assert.Equal(PlatformPlanGrantChange.Created, trail[0].Change);
        Assert.Equal(PlatformPlanGrantChange.Changed, trail[1].Change);
        Assert.Equal("Upgraded for the pilot", trail[1].Reason);
    }

    /// <summary>
    /// The two-business criterion: a decision about one business leaves the other's grant exactly as
    /// it was, and reading one never returns the other's.
    /// </summary>
    [Fact]
    public async Task A_decision_reaches_only_the_business_it_names()
    {
        await WriteAsync(Write(SecondBusinessId, SubscriptionPlan.Starter));
        await WriteAsync(Write(FirstBusinessId, SubscriptionPlan.Pro));

        await using var db = TestAppDbContext.Denied(_options);
        var store = new EfPlatformPlanGrantStore(db);

        var first = await store.FindAsync(FirstBusinessId, CancellationToken.None);
        var second = await store.FindAsync(SecondBusinessId, CancellationToken.None);

        Assert.Equal(SubscriptionPlan.Pro, Assert.Single(first!.Grants).Plan);
        Assert.Equal(SubscriptionPlan.Starter, Assert.Single(second!.Grants).Plan);
    }

    /// <summary>
    /// The lease is as narrow as it claims: while it is open, a write of any other tenant-owned
    /// entity is refused by <c>BusinessOwnershipEnforcer</c> exactly as it is without one, so the
    /// decision cannot carry an unrelated row into the database with it.
    /// </summary>
    [Fact]
    public async Task Another_tenant_owned_row_cannot_ride_along_with_a_decision()
    {
        await using var db = TestAppDbContext.Denied(_options);
        var store = new EfPlatformPlanGrantStore(db);
        await using var transaction = await store.BeginWriteTransactionAsync(CancellationToken.None);

        db.Suppliers.Add(new Supplier { Name = "Smuggled supplier", BusinessId = FirstBusinessId });

        await Assert.ThrowsAsync<CrossBusinessAccessException>(() =>
            store.SaveAsync(Write(FirstBusinessId, SubscriptionPlan.Pro), CancellationToken.None));
    }

    /// <summary>
    /// And it grants nothing to another business: a grant row carrying a different business's key is
    /// refused by the same central check every ordinary insert goes through, rather than being
    /// silently re-owned.
    /// </summary>
    [Fact]
    public async Task A_grant_row_of_another_business_is_refused_while_a_lease_is_open()
    {
        await using var db = TestAppDbContext.Denied(_options);
        var store = new EfPlatformPlanGrantStore(db);
        await using var transaction = await store.BeginWriteTransactionAsync(CancellationToken.None);

        db.BusinessPlanGrants.Add(new BusinessPlanGrant
        {
            BusinessId = SecondBusinessId,
            Plan = SubscriptionPlan.Pro,
            Source = ManualPlanGrantSource.Complimentary,
            StartsAtUtc = Now,
            GrantedAtUtc = Now,
            Revision = 1,
        });

        await Assert.ThrowsAsync<CrossBusinessAccessException>(() =>
            store.SaveAsync(Write(FirstBusinessId, SubscriptionPlan.Pro), CancellationToken.None));
    }

    /// <summary>
    /// Outside a decision the denied scope is unchanged: a grant written without the lease is refused,
    /// so the lease is the only way this table is reachable from a platform request.
    /// </summary>
    [Fact]
    public async Task Without_a_lease_a_denied_scope_still_cannot_write_a_grant()
    {
        await using var db = TestAppDbContext.Denied(_options);

        db.BusinessPlanGrants.Add(new BusinessPlanGrant
        {
            BusinessId = FirstBusinessId,
            Plan = SubscriptionPlan.Pro,
            Source = ManualPlanGrantSource.Complimentary,
            StartsAtUtc = Now,
            GrantedAtUtc = Now,
        });

        await Assert.ThrowsAsync<CrossBusinessAccessException>(() => db.SaveChangesAsync());
    }

    /// <summary>
    /// A lease may be taken only by a request that resolved no business of its own: a member's scope
    /// and a support session's cannot open one at all, so this path is unreachable from inside a
    /// business.
    /// </summary>
    [Fact]
    public async Task A_resolved_or_support_session_scope_cannot_write_a_grant_through_this_adapter()
    {
        await using (var member = TestAppDbContext.For(_options, FirstBusinessId))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new EfPlatformPlanGrantStore(member)
                    .SaveAsync(Write(FirstBusinessId, SubscriptionPlan.Pro), CancellationToken.None));
        }

        await using var support = TestAppDbContext.SupportSession(_options, FirstBusinessId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new EfPlatformPlanGrantStore(support)
                .SaveAsync(Write(FirstBusinessId, SubscriptionPlan.Pro), CancellationToken.None));

        await using var reader = TestAppDbContext.Unrestricted(_options);
        Assert.Empty(await reader.BusinessPlanGrants.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// The audit trail is append-only in practice as well as by intent: there is no path that updates
    /// one, and an update attempted directly under the lease is refused.
    /// </summary>
    [Fact]
    public async Task An_audit_row_cannot_be_changed_under_the_lease()
    {
        await WriteAsync(Write(FirstBusinessId, SubscriptionPlan.Pro));

        await using var db = TestAppDbContext.Denied(_options);
        var store = new EfPlatformPlanGrantStore(db);
        await using var transaction = await store.BeginWriteTransactionAsync(CancellationToken.None);

        var audit = await db.BusinessPlanGrantAudits.IgnoreQueryFilters().SingleAsync();
        audit.Reason = "Rewritten";

        await Assert.ThrowsAsync<CrossBusinessAccessException>(() =>
            store.SaveAsync(
                Write(FirstBusinessId, SubscriptionPlan.Starter) with { Revision = 2 },
                CancellationToken.None));
    }

    /// <summary>
    /// A business's own members read their grant through the ordinary tenant-scoped path of issue
    /// #966, with nothing added: what this surface writes is an ordinary tenant-owned row, and the
    /// other business's scope still sees none of it.
    /// </summary>
    [Fact]
    public async Task What_the_platform_writes_is_read_by_that_business_through_the_ordinary_path()
    {
        await WriteAsync(Write(FirstBusinessId, SubscriptionPlan.Starter));

        await using (var owner = TestAppDbContext.For(_options, FirstBusinessId))
        {
            var grants = await new EfManualPlanGrantStore(owner)
                .ListForCurrentBusinessAsync(CancellationToken.None);

            var grant = Assert.Single(grants);
            Assert.Equal(SubscriptionPlan.Starter, grant.Plan);
            Assert.Equal(ManualPlanGrantSource.Complimentary, grant.Source);
            Assert.True(grant.Period.Covers(Now));
        }

        await using var other = TestAppDbContext.For(_options, SecondBusinessId);
        Assert.Empty(await new EfManualPlanGrantStore(other).ListForCurrentBusinessAsync(CancellationToken.None));
    }

    private async Task WriteAsync(PlatformPlanGrantWrite write)
    {
        await using var db = TestAppDbContext.Denied(_options);
        var store = new EfPlatformPlanGrantStore(db);
        await using var transaction = await store.BeginWriteTransactionAsync(CancellationToken.None);

        await store.SaveAsync(write, CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
    }

    private async Task SeedGrantAsync(int businessId, SubscriptionPlan plan, ManualPlanGrantSource source)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        db.BusinessPlanGrants.Add(new BusinessPlanGrant
        {
            BusinessId = businessId,
            Plan = plan,
            Source = source,
            StartsAtUtc = Now.AddDays(-30),
            GrantedAtUtc = Now.AddDays(-30),
        });

        await db.SaveChangesAsync();
    }

    private static PlatformPlanGrantWrite Write(int businessId, SubscriptionPlan plan)
    {
        Assert.True(ActorIdentity.TryCreate(DirectoryTenantId, AdministratorObjectId, out var actor));

        return new PlatformPlanGrantWrite(
            businessId,
            plan,
            StartsAtUtc: Now,
            EndsAtUtc: null,
            GrantReason: "Complimentary pilot",
            AuditReason: "Complimentary pilot",
            Revision: 1,
            PlatformPlanGrantChange.Created,
            FromPlan: null,
            actor!,
            OccurredAtUtc: Now);
    }

    private static Business NewBusiness(int id, string name) => new()
    {
        Id = id,
        Name = name,
        TimeZoneId = "Australia/Sydney",
        Status = BusinessStatus.Active,
        CreatedAtUtc = Now.AddYears(-1),
    };
}
