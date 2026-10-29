using Inventory.Domain.Subscriptions;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddBusinessSubscriptions</c> (issue #966): the two tenant-owned subscription
/// tables and the one platform table that records the grandfathering step.
///
/// <para>Three claims are worth proving, and the first is the one the issue's own migration note
/// makes: <strong>the migration creates no grant and downgrades no business.</strong> A business that
/// already existed is migrated untouched and the three tables arrive empty, so applying this schema
/// changes nothing anybody can see - which is what makes it safe to deploy before the rollout
/// setting is ever switched on. The second is the uniqueness the rerun safety rests on, read from
/// the schema rather than trusted to the application: one subscription per business, and one
/// grandfathered grant per business through a filtered index. The third is the tenancy decision: both
/// subscription tables carry a business column and the grandfathering record carries none.</para>
/// </summary>
public class AddBusinessSubscriptionsMigrationTests
{
    private const string PreviousMigration = "20261028120000_AddPriceCollectorResults";

    private const string TargetMigration = "AddBusinessSubscriptions";

    private static readonly DateTime Created = new(2026, 2, 10, 4, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Migration_adds_the_three_tables_and_leaves_the_business_that_existed_unchanged()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var before = TestAppDbContext.Unrestricted(options))
        {
            await before.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            var tables = await MigrationSchemaProbe.TableNamesAsync(connection);
            Assert.DoesNotContain("BusinessBillingSubscriptions", tables);
            Assert.DoesNotContain("BusinessPlanGrants", tables);
            Assert.DoesNotContain("SubscriptionGrandfatherings", tables);

            before.Businesses.Add(new Business
            {
                Name = "Vending Co",
                TimeZoneId = "Australia/Sydney",
                Status = BusinessStatus.Active,
                CreatedAtUtc = Created,
            });
            await before.SaveChangesAsync();
        }

        await using (var after = TestAppDbContext.Unrestricted(options))
        {
            await after.GetService<IMigrator>().MigrateAsync(TargetMigration);

            var tables = await MigrationSchemaProbe.TableNamesAsync(connection);
            Assert.Contains("BusinessBillingSubscriptions", tables);
            Assert.Contains("BusinessPlanGrants", tables);
            Assert.Contains("SubscriptionGrandfatherings", tables);

            // All three arrive empty. No grant is created, so no business is entitled to anything by
            // this migration; and no grandfathering record exists, so nothing is enforced either.
            Assert.Equal(0, await MigrationSchemaProbe.RowCountAsync(connection, "BusinessBillingSubscriptions"));
            Assert.Equal(0, await MigrationSchemaProbe.RowCountAsync(connection, "BusinessPlanGrants"));
            Assert.Equal(0, await MigrationSchemaProbe.RowCountAsync(connection, "SubscriptionGrandfatherings"));

            // And the business itself is exactly as it was: no plan column, no status change, no
            // decision instant invented.
            var business = await after.Businesses.AsNoTracking().SingleAsync();
            Assert.Equal("Vending Co", business.Name);
            Assert.Equal(BusinessStatus.Active, business.Status);
            Assert.Equal(Created, business.CreatedAtUtc);
            Assert.Null(business.DecidedAtUtc);
        }
    }

    /// <summary>
    /// The two uniqueness rules the command's "safe to rerun" rests on, read from the schema: a
    /// second subscription for one business is refused, and so is a second grandfathered grant -
    /// while a second complimentary grant is not, because those are a list.
    /// </summary>
    [Fact]
    public async Task The_schema_refuses_a_second_subscription_and_a_second_grandfathered_grant()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await db.GetService<IMigrator>().MigrateAsync(TargetMigration);

        db.Businesses.Add(new Business
        {
            Id = 1,
            Name = "Vending Co",
            TimeZoneId = "Australia/Sydney",
            Status = BusinessStatus.Active,
            CreatedAtUtc = Created,
        });
        await db.SaveChangesAsync();

        db.BusinessBillingSubscriptions.Add(Subscription(businessId: 1));
        await db.SaveChangesAsync();

        db.BusinessBillingSubscriptions.Add(Subscription(businessId: 1));
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        db.BusinessPlanGrants.Add(Grant(businessId: 1, ManualPlanGrantSource.Grandfathered));
        await db.SaveChangesAsync();

        db.BusinessPlanGrants.Add(Grant(businessId: 1, ManualPlanGrantSource.Grandfathered));
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        // Two complimentary grants are legitimate: a business may be given one more than once.
        db.BusinessPlanGrants.AddRange(
            Grant(businessId: 1, ManualPlanGrantSource.Complimentary),
            Grant(businessId: 1, ManualPlanGrantSource.Complimentary));
        await db.SaveChangesAsync();

        Assert.Equal(3, await db.BusinessPlanGrants.CountAsync());
    }

    /// <summary>
    /// The tenancy decision, in the schema: the two per-business tables carry the ownership column
    /// the central filter and stamp use, and the grandfathering record carries none at all - it is
    /// about no business.
    /// </summary>
    [Fact]
    public async Task The_two_business_tables_carry_the_ownership_column_and_the_platform_record_carries_none()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await db.GetService<IMigrator>().MigrateAsync(TargetMigration);

        Assert.Contains(
            nameof(IBusinessOwned.BusinessId),
            await MigrationSchemaProbe.ColumnNamesAsync(connection, "BusinessBillingSubscriptions"));
        Assert.Contains(
            nameof(IBusinessOwned.BusinessId),
            await MigrationSchemaProbe.ColumnNamesAsync(connection, "BusinessPlanGrants"));
        Assert.DoesNotContain(
            nameof(IBusinessOwned.BusinessId),
            await MigrationSchemaProbe.ColumnNamesAsync(connection, "SubscriptionGrandfatherings"));

        // And the grandfathering record's key is not assigned by the database: it is the fixed
        // singleton id, which is what makes two first runs collide instead of recording two cutoffs.
        Assert.DoesNotContain(
            "AUTOINCREMENT",
            await MigrationSchemaProbe.CreateTableSqlAsync(connection, "SubscriptionGrandfatherings"),
            StringComparison.OrdinalIgnoreCase);
    }

    private static BusinessBillingSubscription Subscription(int businessId) => new()
    {
        BusinessId = businessId,
        Plan = SubscriptionPlan.Pro,
        Status = BillingSubscriptionStatus.Active,
        CurrentPeriodStartUtc = Created,
        CurrentPeriodEndUtc = Created.AddMonths(1),
        UpdatedAtUtc = Created,
    };

    private static BusinessPlanGrant Grant(int businessId, ManualPlanGrantSource source) => new()
    {
        BusinessId = businessId,
        Plan = SubscriptionPlan.Pro,
        Source = source,
        StartsAtUtc = Created,
        EndsAtUtc = null,
        GrantedAtUtc = Created,
    };
}
