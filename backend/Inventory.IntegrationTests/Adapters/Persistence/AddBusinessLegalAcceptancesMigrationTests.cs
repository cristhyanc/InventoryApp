using Inventory.Application.Legal;
using Inventory.Domain.Legal;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddBusinessLegalAcceptances</c> (issue #1259), from the schema immediately
/// before it. The migration is additive - one new table and its two indexes - so what has to be
/// proved is that it is additive over a database that already trades: the businesses, the
/// memberships and the invites an existing deployment holds must come through the upgrade untouched,
/// because nobody who already joined is being asked to agree to anything.
///
/// <para><c>EfLegalAcceptanceStoreTests</c> covers the adapter on a database migrated from empty to
/// latest, which is the fresh-install case. This is the other one AGENTS.md § Tests required by
/// change type asks for: the upgrade from the previous schema, with the pre-existing rows checked
/// afterwards.</para>
/// </summary>
public class AddBusinessLegalAcceptancesMigrationTests
{
    private const string PreviousMigration = "20261120080832_AddNayaxSalesProductIdIndex";
    private const string TargetMigration = "20261122000000_AddBusinessLegalAcceptances";

    private const string AcceptanceTable = "BusinessLegalAcceptances";
    private const string BusinessIndex = "IX_BusinessLegalAcceptances_BusinessId";
    private const string AcceptedAtIndex = "IX_BusinessLegalAcceptances_BusinessId_AcceptedAtUtc";

    private const int BusinessId = 1;
    private const int OtherBusinessId = 2;

    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string OwnerOid = "22222222-2222-2222-2222-222222222222";
    private const string JoinerOid = "33333333-3333-3333-3333-333333333333";

    private static readonly DateTime AcceptedAt = new(2026, 11, 22, 3, 30, 0, DateTimeKind.Utc);

    /// <summary>
    /// The upgrade itself: a database at the previous migration, holding two businesses, three
    /// memberships and two invites (one still pending, one already redeemed), gains the acceptance
    /// table and its indexes and loses nothing. The table arrives empty - there is no backfill, and
    /// an acceptance nobody gave would be a false record rather than a convenience.
    /// </summary>
    [Fact]
    public async Task The_upgrade_adds_an_empty_acceptance_table_and_preserves_every_existing_row()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        List<string> tablesBefore;
        await using (var before = TestAppDbContext.Unrestricted(options))
        {
            await before.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            tablesBefore = await MigrationSchemaProbe.TableNamesAsync(connection);
            Assert.DoesNotContain(AcceptanceTable, tablesBefore);

            await SeedPreviousSchemaAsync(connection);
        }

        await using (var after = TestAppDbContext.Unrestricted(options))
        {
            await after.GetService<IMigrator>().MigrateAsync(TargetMigration);

            // The new table and nothing else: every table that was there is still there.
            Assert.Equal(
                tablesBefore.Append(AcceptanceTable).Order(),
                (await MigrationSchemaProbe.TableNamesAsync(connection)).Order());
            Assert.Equal(
                [BusinessIndex, AcceptedAtIndex],
                (await MigrationSchemaProbe.IndexNamesAsync(connection, AcceptanceTable)).Order());
            Assert.Equal(0L, await MigrationSchemaProbe.RowCountAsync(connection, AcceptanceTable));

            // The businesses, read back through the model the upgrade produced.
            var businesses = await after.Businesses
                .AsNoTracking()
                .OrderBy(business => business.Id)
                .ToListAsync();
            Assert.Equal(
                [(BusinessId, "Vending Co", BusinessStatus.Active, "Australia/Sydney"),
                 (OtherBusinessId, "Second Vending Co", BusinessStatus.Active, "Australia/Brisbane")],
                businesses.Select(business =>
                    (business.Id, business.Name, business.Status, business.TimeZoneId)));

            // The memberships, including the revoked one: who may act is not changed by this
            // migration, in either direction.
            var memberships = await after.BusinessMemberships
                .AsNoTracking()
                .OrderBy(membership => membership.Id)
                .ToListAsync();
            Assert.Equal(
                [(BusinessId, OwnerOid, BusinessRole.Owner, true),
                 (BusinessId, JoinerOid, BusinessRole.Operator, true),
                 (OtherBusinessId, OwnerOid, BusinessRole.Owner, false)],
                memberships.Select(membership =>
                    (membership.BusinessId, membership.ObjectId, membership.Role, membership.IsActive)));

            // The invites, with the redeemed one still naming the membership it created and the
            // pending one still live.
            var invites = await after.MemberInvites
                .AsNoTracking()
                .OrderBy(invite => invite.Id)
                .ToListAsync();
            Assert.Equal(
                [(BusinessId, MemberInviteStatus.Used, 2), (BusinessId, MemberInviteStatus.Pending, (int?)null)],
                invites.Select(invite => (invite.BusinessId, invite.Status, invite.RedeemedByMembershipId)));
            Assert.Equal(["used-invite-token-hash", "pending-invite-token-hash"], invites.Select(invite => invite.TokenHash));
        }
    }

    /// <summary>
    /// And the table the upgrade added is the real one: the adapter writes through it under the
    /// denied scope both recording paths carry, the row is stamped with the business the use case
    /// named, and it is readable from that business and from nowhere else - on a database that was
    /// upgraded rather than created.
    /// </summary>
    [Fact]
    public async Task An_acceptance_can_be_recorded_against_a_business_that_predates_the_upgrade()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var before = TestAppDbContext.Unrestricted(options))
        {
            await before.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            await SeedPreviousSchemaAsync(connection);
        }

        await using (var upgrade = TestAppDbContext.Unrestricted(options))
            await upgrade.GetService<IMigrator>().MigrateAsync(TargetMigration);

        await using (var writer = TestAppDbContext.Denied(options))
        {
            Assert.True(ActorIdentity.TryCreate(Tid, JoinerOid, out var identity));
            await new EfLegalAcceptanceStore(writer).RecordAsync(
                new LegalAcceptance(
                    Inventory.Domain.Tenancy.BusinessId.From(BusinessId),
                    identity!,
                    LegalTerms.CurrentVersion,
                    AcceptedAt,
                    LegalAcceptanceContext.InviteRedemption),
                CancellationToken.None);
        }

        await using (var owner = TestAppDbContext.For(options, BusinessId))
        {
            var stored = Assert.Single(await owner.BusinessLegalAcceptances.AsNoTracking().ToListAsync());
            Assert.Equal(
                (BusinessId, Tid, JoinerOid, LegalTerms.CurrentVersion, LegalAcceptanceContext.InviteRedemption),
                (stored.BusinessId, stored.DirectoryTenantId, stored.ObjectId, stored.Version, stored.Context));
            Assert.Equal(AcceptedAt, stored.AcceptedAtUtc);
        }

        await using (var other = TestAppDbContext.For(options, OtherBusinessId))
            Assert.Empty(await other.BusinessLegalAcceptances.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// <c>Down</c> drops the table and leaves the trading rows where they were, so a deployment that
    /// has to step back to the previous release steps back to exactly the schema it came from.
    /// </summary>
    [Fact]
    public async Task Rolling_the_migration_back_drops_only_the_acceptance_table()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        List<string> tablesBefore;
        await using (var before = TestAppDbContext.Unrestricted(options))
        {
            await before.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            tablesBefore = await MigrationSchemaProbe.TableNamesAsync(connection);
            await SeedPreviousSchemaAsync(connection);
        }

        await using (var after = TestAppDbContext.Unrestricted(options))
            await after.GetService<IMigrator>().MigrateAsync(TargetMigration);

        await using (var rollback = TestAppDbContext.Unrestricted(options))
            await rollback.GetService<IMigrator>().MigrateAsync(PreviousMigration);

        Assert.Equal(
            tablesBefore.Order(),
            (await MigrationSchemaProbe.TableNamesAsync(connection)).Order());
        Assert.Equal(2L, await MigrationSchemaProbe.RowCountAsync(connection, "Businesses"));
        Assert.Equal(3L, await MigrationSchemaProbe.RowCountAsync(connection, "BusinessMemberships"));
        Assert.Equal(2L, await MigrationSchemaProbe.RowCountAsync(connection, "MemberInvites"));
    }

    /// <summary>
    /// Two businesses, the memberships a real deployment holds - an owner, somebody who joined on an
    /// invite, and a revoked membership in another business - and the two invites that produced
    /// them. Written as SQL, because the model describes the schema <em>after</em> the migration and
    /// this seed has to be readable by the schema before it.
    /// </summary>
    private static async Task SeedPreviousSchemaAsync(SqliteConnection connection)
    {
        await using var seed = connection.CreateCommand();
        seed.CommandText = $"""
            INSERT INTO Businesses (Id, Name, Status, CreatedAtUtc, TimeZoneId)
                VALUES ({BusinessId}, 'Vending Co', 20, '2026-01-01 00:00:00', 'Australia/Sydney');
            INSERT INTO Businesses (Id, Name, Status, CreatedAtUtc, TimeZoneId)
                VALUES ({OtherBusinessId}, 'Second Vending Co', 20, '2026-02-01 00:00:00', 'Australia/Brisbane');

            INSERT INTO BusinessMemberships
                (Id, BusinessId, DirectoryTenantId, ObjectId, Role, IsActive, CreatedAtUtc, StatusChangedAtUtc)
                VALUES (1, {BusinessId}, '{Tid}', '{OwnerOid}', 40, 1, '2026-01-01 00:00:00', '2026-01-01 00:00:00');
            INSERT INTO BusinessMemberships
                (Id, BusinessId, DirectoryTenantId, ObjectId, Role, IsActive, CreatedAtUtc, StatusChangedAtUtc)
                VALUES (2, {BusinessId}, '{Tid}', '{JoinerOid}', 20, 1, '2026-03-01 00:00:00', '2026-03-01 00:00:00');
            INSERT INTO BusinessMemberships
                (Id, BusinessId, DirectoryTenantId, ObjectId, Role, IsActive, CreatedAtUtc, StatusChangedAtUtc)
                VALUES (3, {OtherBusinessId}, '{Tid}', '{OwnerOid}', 40, 0, '2026-02-01 00:00:00', '2026-02-02 00:00:00');

            INSERT INTO MemberInvites
                (Id, BusinessId, TokenHash, Role, Status, CreatedAtUtc, ExpiresAtUtc,
                 CreatedByDirectoryTenantId, CreatedByObjectId, UsedAtUtc, RedeemedByMembershipId)
                VALUES (1, {BusinessId}, 'used-invite-token-hash', 20, 20, '2026-02-25 00:00:00',
                        '2026-03-04 00:00:00', '{Tid}', '{OwnerOid}', '2026-03-01 00:00:00', 2);
            INSERT INTO MemberInvites
                (Id, BusinessId, TokenHash, Role, Status, CreatedAtUtc, ExpiresAtUtc,
                 CreatedByDirectoryTenantId, CreatedByObjectId)
                VALUES (2, {BusinessId}, 'pending-invite-token-hash', 30, 10, '2026-11-20 00:00:00',
                        '2026-11-27 00:00:00', '{Tid}', '{OwnerOid}');
            """;
        await seed.ExecuteNonQueryAsync();
    }
}
