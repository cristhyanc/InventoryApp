using Inventory.Application.Members;
using Inventory.Application.Tenancy;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Gst;
using InventoryApi.Tests.Application.Members;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The member management of issue #533 under real concurrency, through the real use cases and the
/// real EF adapters.
///
/// This is the test the whole write design exists for. Both rules it leans on are check-then-write
/// rules, and a check-then-write rule that is not serialised is not a rule: two Owners revoking
/// each other at the same instant otherwise both read "there is another Owner" and both write,
/// leaving a business nobody inside it can administer, and two businesses reinstating one person at
/// the same instant both read "they are not a member anywhere" and both write, leaving that person
/// denied access to both. <see cref="MembershipWriteGuard"/> makes the authoritative read, the rule
/// and the mutation one operation, and these tests open the window it has to close.
///
/// It runs against a real SQLite <strong>file</strong> with two independent connections, because
/// that is the only way to have two genuine writers: a shared in-memory database is one connection
/// and the EF InMemory provider has no locking at all. The two operations are released together at
/// a barrier, and each one runs in its own <see cref="AppDbContext"/> exactly as two requests would.
/// </summary>
public class MemberManagementConcurrencyTests : IDisposable
{
    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string FirstOid = "22222222-2222-2222-2222-222222222222";
    private const string SecondOid = "33333333-3333-3333-3333-333333333333";
    private const int FirstBusinessId = 1;
    private const int SecondBusinessId = 2;

    private static readonly DateTime ChangedAt = new(2026, 10, 11, 1, 0, 0, DateTimeKind.Utc);

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"inventory-member-management-{Guid.NewGuid():N}.db");

    private readonly string _connectionString;

    public MemberManagementConcurrencyTests()
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString();

        using var db = TestAppDbContext.Unrestricted(Options());
        db.Database.Migrate();
        db.Businesses.AddRange(
            NewBusiness(FirstBusinessId, "Vending Co"),
            NewBusiness(SecondBusinessId, "Other Vending Co"));
        db.SaveChanges();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Two Owners revoking each other at the same instant. Exactly one revocation may apply, the
    /// other is refused with the Domain's own sentence, and the business still has an active Owner -
    /// which is the whole point, because nothing inside the application could repair a business that
    /// had none.
    /// </summary>
    [Fact]
    public async Task Two_owners_revoking_each_other_leave_an_active_owner()
    {
        Seed(
            NewMembership(FirstBusinessId, FirstOid),
            NewMembership(FirstBusinessId, SecondOid));

        var memberships = ReadMemberships();
        var first = memberships[0].Id;
        var second = memberships[1].Id;

        var failures = await RaceAsync(
            // Each Owner revokes the other, so neither request is about its own row.
            token => RevokeAsync(FirstBusinessId, FirstOid, second, token),
            token => RevokeAsync(FirstBusinessId, SecondOid, first, token));

        var refused = Assert.Single(failures.Where(failure => failure is not null));
        Assert.Single(failures.Where(failure => failure is null));
        Assert.Equal(
            BusinessOwnerRetention.LastActiveOwnerMessage,
            Assert.IsType<DomainConflictException>(refused).Message);

        var after = ReadMemberships();
        Assert.Single(after.Where(membership => membership.IsActive && membership.Role == BusinessRole.Owner));
        // The refused revocation was rolled back whole: the row it targeted is untouched, instant
        // included, rather than left half-applied.
        var survivor = after.Single(membership => membership.IsActive);
        Assert.Equal(survivor.CreatedAtUtc, survivor.StatusChangedAtUtc);
    }

    /// <summary>
    /// The same rule when the two Owners demote each other instead of revoking each other. The rule
    /// is about the state the business is left in, so it covers a role change and a revocation
    /// alike without either operation knowing about the other.
    /// </summary>
    [Fact]
    public async Task Two_owners_demoting_each_other_leave_an_active_owner()
    {
        Seed(
            NewMembership(FirstBusinessId, FirstOid),
            NewMembership(FirstBusinessId, SecondOid));

        var memberships = ReadMemberships();

        var failures = await RaceAsync(
            token => DemoteAsync(FirstOid, memberships[1].Id, token),
            token => DemoteAsync(SecondOid, memberships[0].Id, token));

        Assert.Equal(
            BusinessOwnerRetention.LastActiveOwnerMessage,
            Assert.IsType<DomainConflictException>(
                Assert.Single(failures.Where(failure => failure is not null))).Message);

        var after = ReadMemberships();
        Assert.Single(after.Where(membership => membership.IsActive && membership.Role == BusinessRole.Owner));
        Assert.Single(after.Where(membership => membership.Role == BusinessRole.Manager));
    }

    /// <summary>
    /// One person, revoked in two businesses, reinstated by both Owners at the same instant.
    /// Exactly one reinstatement may apply: a person belongs to one business at a time, and
    /// reinstating them in both would deny them both. The loser is told the same fixed sentence it
    /// would have been told sequentially - not a database error, and certainly not silence.
    /// </summary>
    [Fact]
    public async Task Two_businesses_reinstating_one_person_at_once_leave_exactly_one_active()
    {
        Seed(
            // An Owner in each business, so neither reinstatement is refused by the Owner rule.
            NewMembership(FirstBusinessId, SecondOid),
            NewMembership(FirstBusinessId, FirstOid, BusinessRole.Operator, isActive: false),
            NewMembership(SecondBusinessId, FirstOid, BusinessRole.Operator, isActive: false));

        var memberships = ReadMemberships();
        var inFirst = memberships.Single(m => m.BusinessId == FirstBusinessId && m.ObjectId == FirstOid).Id;
        var inSecond = memberships.Single(m => m.BusinessId == SecondBusinessId).Id;

        var failures = await RaceAsync(
            token => ReinstateAsync(FirstBusinessId, SecondOid, inFirst, token),
            token => ReinstateAsync(SecondBusinessId, SecondOid, inSecond, token));

        var refused = Assert.Single(failures.Where(failure => failure is not null));
        Assert.Single(failures.Where(failure => failure is null));
        Assert.Equal(
            MembershipEligibility.AlreadyAMemberMessage,
            Assert.IsType<DomainConflictException>(refused).Message);

        Assert.Single(ReadMemberships().Where(membership =>
            membership.ObjectId == FirstOid && membership.IsActive));
    }

    /// <summary>
    /// A member change that no rule refuses is committed, writes only its own column, and is
    /// audited once - the sequential half of the same path, so the concurrency tests above are
    /// known to be racing a write that otherwise works.
    /// </summary>
    [Fact]
    public async Task A_permitted_revocation_is_committed_and_audited_once()
    {
        Seed(
            NewMembership(FirstBusinessId, FirstOid),
            NewMembership(FirstBusinessId, SecondOid, BusinessRole.Manager));

        var target = ReadMemberships().Single(membership => membership.Role == BusinessRole.Manager);
        var audit = new FakeMemberManagementAudit();

        await using var db = TestAppDbContext.For(Options(), FirstBusinessId);
        var member = await new RevokeMember(Writer(db, FirstBusinessId, FirstOid, audit))
            .Handle(target.Id, CancellationToken.None);

        Assert.NotNull(member);
        Assert.False(member.IsActive);
        Assert.Equal(BusinessRole.Manager, member.Role);

        var stored = ReadMemberships().Single(membership => membership.Id == target.Id);
        Assert.False(stored.IsActive);
        Assert.Equal(ChangedAt, stored.StatusChangedAtUtc);
        // The targeted update names one column plus the instant that belongs with it, so nothing
        // else on the row can move - the role, the identity pair and the display values included.
        Assert.Equal(BusinessRole.Manager, stored.Role);
        Assert.Equal(target.CreatedAtUtc, stored.CreatedAtUtc);
        Assert.Equal(target.DirectoryTenantId, stored.DirectoryTenantId);
        Assert.Equal(target.ObjectId, stored.ObjectId);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(MemberChange.Revoked, entry.Change);
        Assert.Equal(FirstBusinessId, entry.BusinessId);
        Assert.Equal(target.Id, entry.MembershipId);
        Assert.True(entry.PreviouslyActive);
        Assert.False(entry.NowActive);
        Assert.Equal(BusinessRole.Manager, entry.PreviousRole);
        Assert.Equal(BusinessRole.Manager, entry.NewRole);
        Assert.Equal(ChangedAt, entry.OccurredAtUtc);
    }

    private Task RevokeAsync(int businessId, string actorOid, int membershipId, CancellationToken token)
    {
        return RunAsync(businessId, actorOid, (writer, cancellation) =>
            new RevokeMember(writer).Handle(membershipId, cancellation), token);
    }

    private Task ReinstateAsync(int businessId, string actorOid, int membershipId, CancellationToken token)
    {
        return RunAsync(businessId, actorOid, (writer, cancellation) =>
            new ReinstateMember(writer).Handle(membershipId, cancellation), token);
    }

    private Task DemoteAsync(string actorOid, int membershipId, CancellationToken token)
    {
        return RunAsync(FirstBusinessId, actorOid, (writer, cancellation) =>
            new ChangeMemberRole(writer).Handle(membershipId, "Manager", cancellation), token);
    }

    /// <summary>
    /// One operation in its own <see cref="AppDbContext"/>, scoped to its own business exactly as a
    /// request would be, so the two racing operations share nothing but the database file.
    /// </summary>
    private async Task RunAsync(
        int businessId,
        string actorOid,
        Func<MemberChangeWriter, CancellationToken, Task<BusinessMember?>> operation,
        CancellationToken token)
    {
        await using var db = TestAppDbContext.For(Options(), businessId);
        await operation(Writer(db, businessId, actorOid, new FakeMemberManagementAudit()), token);
    }

    private static MemberChangeWriter Writer(
        AppDbContext db,
        int businessId,
        string actorOid,
        FakeMemberManagementAudit audit) =>
        new(
            new FakeCurrentBusinessProvider(businessId),
            FakeAuthenticatedActorAccessor.Identified(Actor(actorOid)),
            new EfBusinessMemberStore(db),
            new MembershipWriteGuard(new EfBusinessMembershipWriteStore(db)),
            audit,
            new FakeClock(ChangedAt));

    /// <summary>
    /// Runs the operations released at the same instant, and returns the failure each one produced
    /// (<c>null</c> for the one that succeeded).
    /// </summary>
    private static async Task<Exception?[]> RaceAsync(params Func<CancellationToken, Task>[] operations)
    {
        using var barrier = new Barrier(operations.Length);

        var attempts = operations
            .Select(operation => Task.Run(async () =>
            {
                barrier.SignalAndWait();
                try
                {
                    await operation(CancellationToken.None);
                    return (Exception?)null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            }))
            .ToArray();

        return await Task.WhenAll(attempts);
    }

    private DbContextOptions<AppDbContext> Options() =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connectionString).Options;

    private static ActorIdentity Actor(string oid)
    {
        Assert.True(ActorIdentity.TryCreate(Tid, oid, out var actor));
        return actor!;
    }

    private static Business NewBusiness(int id, string name) =>
        new()
        {
            Id = id,
            Name = name,
            TimeZoneId = "Australia/Sydney",
            Status = BusinessStatus.Active,
            CreatedAtUtc = new DateTime(2026, 2, 10, 4, 0, 0, DateTimeKind.Utc),
        };

    private static BusinessMembership NewMembership(
        int businessId,
        string oid,
        BusinessRole role = BusinessRole.Owner,
        bool isActive = true) =>
        new()
        {
            BusinessId = businessId,
            DirectoryTenantId = Tid,
            ObjectId = oid,
            Role = role,
            IsActive = isActive,
            CreatedAtUtc = new DateTime(2026, 2, 10, 4, 0, 0, DateTimeKind.Utc),
            StatusChangedAtUtc = new DateTime(2026, 2, 10, 4, 0, 0, DateTimeKind.Utc),
        };

    private void Seed(params BusinessMembership[] memberships)
    {
        using var db = TestAppDbContext.Unrestricted(Options());
        db.BusinessMemberships.AddRange(memberships);
        db.SaveChanges();
    }

    private List<BusinessMembership> ReadMemberships()
    {
        using var db = TestAppDbContext.Unrestricted(Options());
        return db.BusinessMemberships.AsNoTracking().OrderBy(membership => membership.Id).ToList();
    }
}
