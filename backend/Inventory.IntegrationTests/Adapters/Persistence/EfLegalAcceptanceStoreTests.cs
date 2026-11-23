using Inventory.Application.Legal;
using Inventory.Domain.Legal;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The acceptance-record adapter of issue #1259 against relational SQLite, with the real migration
/// history.
///
/// <para>Every test here runs it over a <strong>denied</strong> context, because that is the only
/// scope either path that records an acceptance ever carries: somebody applying for a business has
/// no membership, and somebody joining one does not have it yet. The acceptance row is nevertheless
/// tenant-owned, so what these tests are really about is the narrow write window - that it stamps
/// the business the operation named, that it is the only thing it opens, and that it closes
/// again.</para>
///
/// <para>Two businesses are seeded throughout, so the isolation assertions mean something: an
/// acceptance recorded for one names that one, and the row a <em>second</em> business's lease would
/// write is not reachable from the first's.</para>
/// </summary>
public class EfLegalAcceptanceStoreTests
{
    private const int BusinessId = 1;
    private const int OtherBusinessId = 2;

    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string Oid = "22222222-2222-2222-2222-222222222222";

    private static readonly DateTime CreatedAt = new(2026, 11, 20, 1, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime AcceptedAt = new(2026, 11, 22, 3, 30, 0, DateTimeKind.Utc);

    /// <summary>
    /// The write itself, under the denied scope: one row, carrying the business the use case named,
    /// the Entra pair, the version, the instant and the context - and the business key stamped by
    /// the enforcer rather than set by the adapter.
    /// </summary>
    [Theory]
    [InlineData(LegalAcceptanceContext.BusinessApplication)]
    [InlineData(LegalAcceptanceContext.InviteRedemption)]
    public async Task It_records_one_acceptance_for_the_business_the_use_case_named(
        LegalAcceptanceContext context)
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using (var db = TestAppDbContext.Denied(options))
            {
                await new EfLegalAcceptanceStore(db).RecordAsync(Acceptance(context: context), CancellationToken.None);
            }

            var stored = Assert.Single(await AcceptancesAsync(options));
            Assert.Equal(BusinessId, stored.BusinessId);
            Assert.Equal(Tid, stored.DirectoryTenantId);
            Assert.Equal(Oid, stored.ObjectId);
            Assert.Equal(LegalTerms.CurrentVersion, stored.Version);
            Assert.Equal(context, stored.Context);
            Assert.Equal(AcceptedAt, stored.AcceptedAtUtc);
            Assert.Equal(DateTimeKind.Utc, stored.AcceptedAtUtc.Kind);
        }
    }

    /// <summary>
    /// The row commits with the caller's transaction and not on its own: the adapter saves inside
    /// whatever transaction the use case opened, so a rolled-back application or redemption leaves
    /// no agreement behind. This is the direct proof of that, made by rolling the transaction back
    /// after the acceptance has actually been written.
    /// </summary>
    [Fact]
    public async Task An_acceptance_written_in_a_rolled_back_transaction_is_not_persisted()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using (var db = TestAppDbContext.Denied(options))
            {
                await using var transaction = await db.Database.BeginTransactionAsync();
                await new EfLegalAcceptanceStore(db).RecordAsync(Acceptance(), CancellationToken.None);
                await transaction.RollbackAsync();
            }

            Assert.Empty(await AcceptancesAsync(options));
        }
    }

    /// <summary>
    /// Two acceptances for one business are both kept. A person whose membership was revoked and who
    /// rejoins on a new invite agreed again, and that second agreement is a separate fact - which is
    /// why there is deliberately no unique index over the actor pair.
    /// </summary>
    [Fact]
    public async Task One_identity_may_record_more_than_one_acceptance_for_one_business()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using (var db = TestAppDbContext.Denied(options))
            {
                var store = new EfLegalAcceptanceStore(db);
                await store.RecordAsync(Acceptance(), CancellationToken.None);
                await store.RecordAsync(
                    Acceptance(acceptedAtUtc: AcceptedAt.AddDays(1)),
                    CancellationToken.None);
            }

            var stored = await AcceptancesAsync(options);
            Assert.Equal(2, stored.Count);
            Assert.Equal([AcceptedAt, AcceptedAt.AddDays(1)], stored.Select(row => row.AcceptedAtUtc));
        }
    }

    /// <summary>
    /// The window is open for one <c>SaveChanges</c> and closes again: a tenant-owned write attempted
    /// on the same denied context afterwards is refused exactly as it is without a lease. Without
    /// this, recording an acceptance would quietly unlock the rest of the request.
    /// </summary>
    [Fact]
    public async Task The_write_window_closes_again_and_unlocks_nothing_else()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Denied(options);
            await new EfLegalAcceptanceStore(db).RecordAsync(Acceptance(), CancellationToken.None);

            db.Categories.Add(new Category { Name = "Snacks" });

            await Assert.ThrowsAsync<CrossBusinessAccessException>(() => db.SaveChangesAsync());
        }
    }

    /// <summary>
    /// And while it <em>is</em> open it unlocks nothing else either: a second tenant-owned entity
    /// tracked beside the acceptance takes the whole save down, because the window is one entity type
    /// and inserts of it alone.
    /// </summary>
    [Fact]
    public async Task Another_tenant_owned_row_tracked_beside_the_acceptance_is_refused()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using (var db = TestAppDbContext.Denied(options))
            {
                db.Categories.Add(new Category { Name = "Snacks" });

                await Assert.ThrowsAsync<CrossBusinessAccessException>(() =>
                    new EfLegalAcceptanceStore(db).RecordAsync(Acceptance(), CancellationToken.None));
            }

            Assert.Empty(await AcceptancesAsync(options));
        }
    }

    /// <summary>
    /// A scope that already names a business is not in either of the two moments an acceptance is
    /// given, so it cannot take the window at all - which is what keeps an ordinary member's request
    /// from recording an agreement for anybody.
    /// </summary>
    [Fact]
    public async Task A_resolved_scope_cannot_record_an_acceptance_at_all()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using (var db = TestAppDbContext.For(options, BusinessId))
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    new EfLegalAcceptanceStore(db).RecordAsync(Acceptance(), CancellationToken.None));
            }

            Assert.Empty(await AcceptancesAsync(options));
        }
    }

    /// <summary>
    /// Nothing reads an acceptance back through the boundary. The row is tenant-owned, so the central
    /// query filter is fully in force over it: the denied scope that wrote it reads none of them, and
    /// a scope resolved to the <em>other</em> business reads none of this business's.
    /// </summary>
    [Fact]
    public async Task An_acceptance_is_not_readable_from_a_denied_scope_or_from_another_business()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using (var writer = TestAppDbContext.Denied(options))
            {
                await new EfLegalAcceptanceStore(writer).RecordAsync(Acceptance(), CancellationToken.None);
            }

            await using (var denied = TestAppDbContext.Denied(options))
            {
                Assert.Empty(await denied.BusinessLegalAcceptances.ToListAsync());
            }

            await using (var other = TestAppDbContext.For(options, OtherBusinessId))
            {
                Assert.Empty(await other.BusinessLegalAcceptances.ToListAsync());
            }

            await using (var owner = TestAppDbContext.For(options, BusinessId))
            {
                Assert.Single(await owner.BusinessLegalAcceptances.ToListAsync());
            }
        }
    }

    private static LegalAcceptance Acceptance(
        int businessId = BusinessId,
        DateTime? acceptedAtUtc = null,
        LegalAcceptanceContext context = LegalAcceptanceContext.BusinessApplication)
    {
        Assert.True(ActorIdentity.TryCreate(Tid, Oid, out var identity));

        return new LegalAcceptance(
            Inventory.Domain.Tenancy.BusinessId.From(businessId),
            identity!,
            LegalTerms.CurrentVersion,
            acceptedAtUtc ?? AcceptedAt,
            context);
    }

    private static async Task<IReadOnlyList<BusinessLegalAcceptance>> AcceptancesAsync(
        DbContextOptions<AppDbContext> options)
    {
        await using var db = TestAppDbContext.Unrestricted(options);
        return await db.BusinessLegalAcceptances
            .AsNoTracking()
            .OrderBy(acceptance => acceptance.Id)
            .ToListAsync();
    }

    private static async Task<(SqliteConnection Connection, DbContextOptions<AppDbContext> Options)> CreateSqliteAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var setup = TestAppDbContext.Unrestricted(options);
        await setup.Database.MigrateAsync();
        setup.Businesses.AddRange(
            Business(BusinessId, "Vending Co"),
            Business(OtherBusinessId, "Second Vending Co"));
        await setup.SaveChangesAsync();
        return (connection, options);
    }

    private static Business Business(int id, string name) =>
        new()
        {
            Id = id,
            Name = name,
            TimeZoneId = "Australia/Sydney",
            Status = BusinessStatus.Active,
            CreatedAtUtc = CreatedAt,
        };
}
