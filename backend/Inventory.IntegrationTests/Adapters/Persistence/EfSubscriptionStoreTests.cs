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
/// The three subscription adapters of issue #966 against relational SQLite with the real migration
/// history: the billing-subscription read, the manual-grant read and its one write, and the
/// grandfathering record.
///
/// <para>Relational rather than a model double, for the reason the other store tests in this folder
/// are: the filtered unique index that refuses a second grandfathered grant, the primary key that
/// refuses a second grandfathering row, and the tenant query filter that keeps one business's plan
/// away from another are all properties of a real database, and "a rerun changes nothing" is a claim
/// about what a table actually contains.</para>
///
/// <para><strong>Two businesses throughout</strong> (AGENTS.md § Tenant ownership and data
/// isolation): every read is made through a context scoped to one of them, so a plan leaking across
/// the boundary would fail a test rather than be invisible.</para>
/// </summary>
public class EfSubscriptionStoreTests : IAsyncLifetime
{
    private const int FirstBusinessId = 1;
    private const int SecondBusinessId = 2;

    private static readonly DateTime Cutoff = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 11, 1, 2, 30, 0, DateTimeKind.Utc);

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

    #region The grandfathering record

    [Fact]
    public async Task With_no_record_the_grandfathering_store_reports_nothing_stored()
    {
        await using var db = TestAppDbContext.Denied(_options);

        Assert.Null(await new EfSubscriptionGrandfatheringStore(db).FindAsync(CancellationToken.None));
    }

    /// <summary>
    /// The cutoff is fixed once: a second start reads the recorded instant back rather than moving
    /// it, which is what makes a rerun grandfather exactly the businesses the first run would have.
    /// </summary>
    [Fact]
    public async Task A_second_start_reuses_the_recorded_cutoff_and_never_moves_it()
    {
        await using var db = TestAppDbContext.Denied(_options);
        var store = new EfSubscriptionGrandfatheringStore(db);

        var first = await store.StartAsync(Cutoff, CancellationToken.None);
        var second = await store.StartAsync(Cutoff.AddDays(30), CancellationToken.None);

        Assert.Equal(Cutoff, first.CutoffUtc);
        Assert.Equal(Cutoff, second.CutoffUtc);
        Assert.False(second.IsComplete);

        await using var reader = TestAppDbContext.Unrestricted(_options);
        var row = await reader.SubscriptionGrandfatherings.AsNoTracking().SingleAsync();
        Assert.Equal(SubscriptionGrandfathering.SingletonId, row.Id);
        Assert.Equal(Cutoff, row.CutoffUtc);
        Assert.Null(row.CompletedAtUtc);
    }

    [Fact]
    public async Task Completing_stamps_the_marker_and_the_count_and_a_second_completion_changes_neither()
    {
        await using var db = TestAppDbContext.Denied(_options);
        var store = new EfSubscriptionGrandfatheringStore(db);

        await store.StartAsync(Cutoff, CancellationToken.None);
        var completed = await store.CompleteAsync(Now, grantedBusinessCount: 2, CancellationToken.None);

        Assert.True(completed.IsComplete);
        Assert.Equal(Now, completed.CompletedAtUtc);
        Assert.Equal(2, completed.GrantedBusinessCount);

        // A rerun after completion finished nothing new, so neither the instant nor the count moves.
        var again = await store.CompleteAsync(Now.AddHours(1), grantedBusinessCount: 99, CancellationToken.None);

        Assert.Equal(Now, again.CompletedAtUtc);
        Assert.Equal(2, again.GrantedBusinessCount);
    }

    /// <summary>
    /// A completion marker with no cutoff beside it would claim a step nobody started had finished,
    /// so it is refused rather than written.
    /// </summary>
    [Fact]
    public async Task Completing_with_no_recorded_cutoff_is_refused()
    {
        await using var db = TestAppDbContext.Denied(_options);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new EfSubscriptionGrandfatheringStore(db)
                .CompleteAsync(Now, grantedBusinessCount: 0, CancellationToken.None));

        await using var reader = TestAppDbContext.Unrestricted(_options);
        Assert.Empty(await reader.SubscriptionGrandfatherings.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// The record is read under a <em>denied</em> scope, which is what a human-run command and the
    /// startup check both carry, and under a resolved one, which is what a member's request carries:
    /// the row is about no business, so both see the same answer.
    /// </summary>
    [Fact]
    public async Task The_record_reads_the_same_under_a_denied_and_a_resolved_scope()
    {
        await using (var writer = TestAppDbContext.Denied(_options))
        {
            await new EfSubscriptionGrandfatheringStore(writer).StartAsync(Cutoff, CancellationToken.None);
            await new EfSubscriptionGrandfatheringStore(writer)
                .CompleteAsync(Now, grantedBusinessCount: 1, CancellationToken.None);
        }

        await using var denied = TestAppDbContext.Denied(_options);
        await using var resolved = TestAppDbContext.For(_options, FirstBusinessId);

        var fromDenied = await new EfSubscriptionGrandfatheringStore(denied).FindAsync(CancellationToken.None);
        var fromResolved = await new EfSubscriptionGrandfatheringStore(resolved).FindAsync(CancellationToken.None);

        Assert.Equal(fromDenied, fromResolved);
        Assert.True(fromResolved!.IsComplete);
    }

    #endregion

    #region Manual grants

    [Fact]
    public async Task The_grandfathered_grant_is_written_once_and_a_rerun_writes_nothing()
    {
        await using var db = TestAppDbContext.For(_options, FirstBusinessId);
        var store = new EfManualPlanGrantStore(db);

        Assert.True(await store.GrantGrandfatheredPlanIfMissingAsync(Cutoff, Now, CancellationToken.None));
        Assert.False(await store.GrantGrandfatheredPlanIfMissingAsync(
            Cutoff.AddDays(5),
            Now.AddDays(5),
            CancellationToken.None));

        await using var reader = TestAppDbContext.Unrestricted(_options);
        var grant = await reader.BusinessPlanGrants.AsNoTracking().SingleAsync();

        Assert.Equal(FirstBusinessId, grant.BusinessId);
        Assert.Equal(SubscriptionPlan.Pro, grant.Plan);
        Assert.Equal(ManualPlanGrantSource.Grandfathered, grant.Source);
        // The rerun rewrote nothing at all: not the start, not the granted instant.
        Assert.Equal(Cutoff, grant.StartsAtUtc);
        Assert.Equal(Now, grant.GrantedAtUtc);
        Assert.Null(grant.EndsAtUtc);
    }

    /// <summary>
    /// The end date an owner sets later survives a rerun. This is the specific way a "safe to
    /// rerun" command could still do harm - by refreshing the grant it found - so it is asserted
    /// rather than assumed.
    /// </summary>
    [Fact]
    public async Task A_rerun_leaves_an_end_date_an_owner_has_since_set_untouched()
    {
        await using (var db = TestAppDbContext.For(_options, FirstBusinessId))
        {
            await new EfManualPlanGrantStore(db)
                .GrantGrandfatheredPlanIfMissingAsync(Cutoff, Now, CancellationToken.None);
        }

        var ends = Cutoff.AddMonths(6);
        await using (var owner = TestAppDbContext.For(_options, FirstBusinessId))
        {
            var grant = await owner.BusinessPlanGrants.SingleAsync();
            grant.EndsAtUtc = ends;
            await owner.SaveChangesAsync();
        }

        await using (var rerun = TestAppDbContext.For(_options, FirstBusinessId))
        {
            Assert.False(await new EfManualPlanGrantStore(rerun)
                .GrantGrandfatheredPlanIfMissingAsync(Cutoff, Now.AddDays(1), CancellationToken.None));
        }

        await using var reader = TestAppDbContext.Unrestricted(_options);
        Assert.Equal(ends, (await reader.BusinessPlanGrants.AsNoTracking().SingleAsync()).EndsAtUtc);
    }

    /// <summary>
    /// A complimentary grant does not satisfy the grandfathering step, and the step does not disturb
    /// it: the two sources are different records of different decisions.
    /// </summary>
    [Fact]
    public async Task A_complimentary_grant_neither_satisfies_nor_is_changed_by_the_step()
    {
        await using (var seed = TestAppDbContext.For(_options, FirstBusinessId))
        {
            seed.BusinessPlanGrants.Add(new BusinessPlanGrant
            {
                Plan = SubscriptionPlan.Starter,
                Source = ManualPlanGrantSource.Complimentary,
                StartsAtUtc = Cutoff.AddMonths(-1),
                EndsAtUtc = Cutoff.AddMonths(1),
                Reason = "Trialling the reports",
                GrantedAtUtc = Cutoff.AddMonths(-1),
            });
            await seed.SaveChangesAsync();
        }

        await using var db = TestAppDbContext.For(_options, FirstBusinessId);
        Assert.True(await new EfManualPlanGrantStore(db)
            .GrantGrandfatheredPlanIfMissingAsync(Cutoff, Now, CancellationToken.None));

        var grants = await new EfManualPlanGrantStore(db).ListForCurrentBusinessAsync(CancellationToken.None);

        Assert.Equal(2, grants.Count);
        Assert.Contains(
            grants,
            grant => grant.Source == ManualPlanGrantSource.Complimentary
                && grant.Plan == SubscriptionPlan.Starter
                && grant.Period.EndsAtUtc == Cutoff.AddMonths(1));
        Assert.Contains(
            grants,
            grant => grant.Source == ManualPlanGrantSource.Grandfathered
                && grant.Plan == SubscriptionPlan.Pro
                && grant.Period.EndsAtUtc is null);
    }

    /// <summary>
    /// One business's grants are invisible to the other, and the write is stamped with the scope's
    /// business rather than with anything a caller supplied - the whole of what makes this an
    /// ordinary tenant-owned table.
    /// </summary>
    [Fact]
    public async Task One_businesss_grants_are_invisible_to_the_other()
    {
        await using (var first = TestAppDbContext.For(_options, FirstBusinessId))
        {
            await new EfManualPlanGrantStore(first)
                .GrantGrandfatheredPlanIfMissingAsync(Cutoff, Now, CancellationToken.None);
        }

        await using var second = TestAppDbContext.For(_options, SecondBusinessId);
        var store = new EfManualPlanGrantStore(second);

        Assert.Empty(await store.ListForCurrentBusinessAsync(CancellationToken.None));

        // And granting the second business its own does not touch the first's.
        Assert.True(await store.GrantGrandfatheredPlanIfMissingAsync(Cutoff, Now, CancellationToken.None));

        await using var reader = TestAppDbContext.Unrestricted(_options);
        var grants = await reader.BusinessPlanGrants.AsNoTracking().OrderBy(grant => grant.Id).ToListAsync();
        Assert.Equal([FirstBusinessId, SecondBusinessId], grants.Select(grant => grant.BusinessId));
    }

    /// <summary>
    /// A denied scope reads no grant and writes none: an unresolved business reads nothing and
    /// writes nothing (AGENTS.md § Tenant ownership and data isolation).
    /// </summary>
    [Fact]
    public async Task A_denied_scope_reads_no_grant_and_cannot_write_one()
    {
        await using (var first = TestAppDbContext.For(_options, FirstBusinessId))
        {
            await new EfManualPlanGrantStore(first)
                .GrantGrandfatheredPlanIfMissingAsync(Cutoff, Now, CancellationToken.None);
        }

        await using var denied = TestAppDbContext.Denied(_options);
        var store = new EfManualPlanGrantStore(denied);

        Assert.Empty(await store.ListForCurrentBusinessAsync(CancellationToken.None));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            store.GrantGrandfatheredPlanIfMissingAsync(Cutoff, Now, CancellationToken.None));

        await using var reader = TestAppDbContext.Unrestricted(_options);
        Assert.Single(await reader.BusinessPlanGrants.AsNoTracking().ToListAsync());
    }

    #endregion

    #region The billing subscription

    [Fact]
    public async Task With_no_row_the_subscription_store_reports_nothing_stored()
    {
        await using var db = TestAppDbContext.For(_options, FirstBusinessId);

        Assert.Null(await new EfBillingSubscriptionStore(db)
            .FindForCurrentBusinessAsync(CancellationToken.None));
    }

    /// <summary>
    /// The row is reported as stored - plan, status and both period instants, with the end marked
    /// UTC on the way out - and the adapter interprets none of it.
    /// </summary>
    [Fact]
    public async Task The_subscription_is_reported_exactly_as_stored()
    {
        await using (var seed = TestAppDbContext.For(_options, FirstBusinessId))
        {
            seed.BusinessBillingSubscriptions.Add(new BusinessBillingSubscription
            {
                Plan = SubscriptionPlan.Starter,
                Status = BillingSubscriptionStatus.Cancelled,
                CurrentPeriodStartUtc = Cutoff,
                CurrentPeriodEndUtc = Cutoff.AddMonths(1),
                UpdatedAtUtc = Now,
            });
            await seed.SaveChangesAsync();
        }

        await using var db = TestAppDbContext.For(_options, FirstBusinessId);
        var subscription = await new EfBillingSubscriptionStore(db)
            .FindForCurrentBusinessAsync(CancellationToken.None);

        Assert.NotNull(subscription);
        Assert.Equal(SubscriptionPlan.Starter, subscription.Plan);
        // A cancelled subscription is reported as cancelled rather than hidden: what it entitles is
        // the Domain policy's decision.
        Assert.Equal(BillingSubscriptionStatus.Cancelled, subscription.Status);
        Assert.Equal(Cutoff, subscription.Period.StartsAtUtc);
        Assert.Equal(Cutoff.AddMonths(1), subscription.Period.EndsAtUtc);
        Assert.Equal(DateTimeKind.Utc, subscription.Period.StartsAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, subscription.Period.EndsAtUtc!.Value.Kind);
    }

    [Fact]
    public async Task One_businesss_subscription_is_invisible_to_the_other()
    {
        await using (var seed = TestAppDbContext.For(_options, FirstBusinessId))
        {
            seed.BusinessBillingSubscriptions.Add(new BusinessBillingSubscription
            {
                Plan = SubscriptionPlan.Pro,
                Status = BillingSubscriptionStatus.Active,
                CurrentPeriodStartUtc = Cutoff,
                CurrentPeriodEndUtc = null,
                UpdatedAtUtc = Now,
            });
            await seed.SaveChangesAsync();
        }

        await using var second = TestAppDbContext.For(_options, SecondBusinessId);

        Assert.Null(await new EfBillingSubscriptionStore(second)
            .FindForCurrentBusinessAsync(CancellationToken.None));
    }

    /// <summary>
    /// A support session resolves the viewed business, so the plan a supporter reads is that
    /// business's own (issue #551, decided 29 October 2026) - and never the administrator's own
    /// business's.
    /// </summary>
    [Fact]
    public async Task A_support_session_reads_the_viewed_businesss_own_plan()
    {
        await using (var seed = TestAppDbContext.For(_options, SecondBusinessId))
        {
            await new EfManualPlanGrantStore(seed)
                .GrantGrandfatheredPlanIfMissingAsync(Cutoff, Now, CancellationToken.None);
        }

        await using var session = TestAppDbContext.SupportSession(_options, SecondBusinessId);
        var grants = await new EfManualPlanGrantStore(session)
            .ListForCurrentBusinessAsync(CancellationToken.None);

        Assert.Equal(SubscriptionPlan.Pro, Assert.Single(grants).Plan);

        await using var otherSession = TestAppDbContext.SupportSession(_options, FirstBusinessId);
        Assert.Empty(await new EfManualPlanGrantStore(otherSession)
            .ListForCurrentBusinessAsync(CancellationToken.None));
    }

    /// <summary>
    /// And a support session still writes nothing: the read-only rule is the enforcer's, not
    /// something each store repeats.
    /// </summary>
    [Fact]
    public async Task A_support_session_cannot_write_a_grant()
    {
        await using var session = TestAppDbContext.SupportSession(_options, FirstBusinessId);

        await Assert.ThrowsAnyAsync<Exception>(() => new EfManualPlanGrantStore(session)
            .GrantGrandfatheredPlanIfMissingAsync(Cutoff, Now, CancellationToken.None));

        await using var reader = TestAppDbContext.Unrestricted(_options);
        Assert.Empty(await reader.BusinessPlanGrants.AsNoTracking().ToListAsync());
    }

    #endregion

    private static Business NewBusiness(int id, string name) => new()
    {
        Id = id,
        Name = name,
        TimeZoneId = "Australia/Sydney",
        Status = BusinessStatus.Active,
        CreatedAtUtc = new DateTime(2026, 2, 10, 4, 0, 0, DateTimeKind.Utc),
    };
}
