using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

// The entity and the Application's reported invite share a name, in different layers. This harness
// seeds and reads rows, so the row gets the alias.
using MemberInviteRow = Inventory.Infrastructure.Models.MemberInvite;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Two genuine concurrent writers against one real SQLite database, for the membership rules that
/// are only rules if they are serialised (issue #522's shared write path, issue #532's invite
/// redemption, and issue #507's self-service application next).
///
/// <para><strong>Why a file and not a shared in-memory database.</strong> A shared in-memory SQLite
/// database is one connection, and the EF InMemory provider has no locking at all, so neither can
/// have two writers - and a race test that cannot race passes whatever the code does. This harness
/// therefore creates a temporary file, migrates it with the real migration history, and hands out
/// <see cref="Options"/> per operation so each writer opens its own connection.</para>
///
/// <para><strong>Why it is shared.</strong> Every one of these races is the same shape: seed a
/// world, release two operations at the same instant, then assert that exactly one won and that the
/// loser left nothing behind. Issue #532 uses it for "the same invite twice" and "two invites, one
/// identity"; issue #507 extends it for "invite versus application", which needs the same two
/// businesses, the same identity seeding and the same barrier - and must not grow a second,
/// subtly-different copy of them. Add a seeding or read-back helper here when a new race needs one;
/// keep the assertions in the test.</para>
///
/// <para>Dispose it at the end of a test class (it is <see cref="IAsyncDisposable"/>): that clears
/// the SQLite connection pool and deletes the file.</para>
/// </summary>
internal sealed class MembershipWriteRaceHarness : IAsyncDisposable
{
    /// <summary>The one Entra directory every identity in these tests signs in from.</summary>
    public const string DirectoryTenantId = "11111111-1111-1111-1111-111111111111";

    private readonly string _databasePath;
    private readonly string _connectionString;

    /// <summary>
    /// Creates the database, applies the real migration history, and seeds one
    /// <see cref="BusinessStatus.Active"/> business per id given. A race needs at least two to be
    /// able to tell "one active membership per identity" from "one per business".
    /// </summary>
    private MembershipWriteRaceHarness(string databasePath, string connectionString, int[] businessIds)
    {
        _databasePath = databasePath;
        _connectionString = connectionString;

        using var db = TestAppDbContext.Unrestricted(Options);
        db.Database.Migrate();
        db.Businesses.AddRange(businessIds.Select(id => new Business
        {
            Id = id,
            Name = $"Business {id}",
            TimeZoneId = "Australia/Sydney",
            Status = BusinessStatus.Active,
            CreatedAtUtc = SeededAt,
        }));
        db.SaveChanges();
    }

    /// <summary>The instant every seeded row records, so nothing depends on the wall clock.</summary>
    public static DateTime SeededAt { get; } = new(2026, 10, 12, 1, 0, 0, DateTimeKind.Utc);

    public static MembershipWriteRaceHarness Create(params int[] businessIds)
    {
        var path = Path.Combine(Path.GetTempPath(), $"inventory-membership-race-{Guid.NewGuid():N}.db");
        return new MembershipWriteRaceHarness(
            path,
            new SqliteConnectionStringBuilder { DataSource = path }.ToString(),
            businessIds);
    }

    /// <summary>
    /// Options for one writer. Asked for per operation on purpose: each racing operation needs its
    /// own <c>AppDbContext</c> over its own connection, which is what makes them two writers rather
    /// than one.
    /// </summary>
    public DbContextOptions<AppDbContext> Options =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connectionString).Options;

    /// <summary>The identity of an actor in this harness's one directory.</summary>
    public static ActorIdentity Actor(string objectId)
    {
        Assert.True(ActorIdentity.TryCreate(DirectoryTenantId, objectId, out var actor));
        return actor!;
    }

    /// <summary>Seeds one membership row, active or revoked, outside any boundary.</summary>
    public int SeedMembership(
        int businessId,
        string objectId,
        BusinessRole role = BusinessRole.Owner,
        bool isActive = true)
    {
        using var db = TestAppDbContext.Unrestricted(Options);
        var membership = new BusinessMembership
        {
            BusinessId = businessId,
            DirectoryTenantId = DirectoryTenantId,
            ObjectId = objectId,
            Role = role,
            IsActive = isActive,
            CreatedAtUtc = SeededAt,
            StatusChangedAtUtc = SeededAt,
        };

        db.BusinessMemberships.Add(membership);
        db.SaveChanges();
        return membership.Id;
    }

    /// <summary>
    /// Seeds one invite of <paramref name="businessId"/> whose token is
    /// <paramref name="token"/>, stored the way a real creation stores it - as the hash alone, so a
    /// race test presents the same thing a person opening the link presents.
    /// </summary>
    public int SeedInvite(
        int businessId,
        string token,
        BusinessRole role = BusinessRole.Manager,
        MemberInviteStatus status = MemberInviteStatus.Pending,
        DateTime? expiresAtUtc = null)
    {
        using var db = TestAppDbContext.Unrestricted(Options);
        var invite = new MemberInviteRow
        {
            BusinessId = businessId,
            Role = role,
            TokenHash = MemberInviteToken.Hash(token),
            Status = status,
            CreatedByDirectoryTenantId = DirectoryTenantId,
            CreatedByObjectId = "99999999-9999-9999-9999-999999999999",
            CreatedAtUtc = SeededAt,
            ExpiresAtUtc = expiresAtUtc ?? MemberInvitePolicy.ExpiresAt(SeededAt),
        };

        db.MemberInvites.Add(invite);
        db.SaveChanges();
        return invite.Id;
    }

    /// <summary>Sets a seeded business's lifecycle status, for the races that need a closed one.</summary>
    public void SetBusinessStatus(int businessId, BusinessStatus status)
    {
        using var db = TestAppDbContext.Unrestricted(Options);
        db.Businesses.Single(business => business.Id == businessId).Status = status;
        db.SaveChanges();
    }

    /// <summary>Every membership row, of every business, in key order.</summary>
    public List<BusinessMembership> Memberships()
    {
        using var db = TestAppDbContext.Unrestricted(Options);
        return db.BusinessMemberships.AsNoTracking().OrderBy(membership => membership.Id).ToList();
    }

    /// <summary>Every invite row, of every business, in key order.</summary>
    public List<MemberInviteRow> Invites()
    {
        using var db = TestAppDbContext.Unrestricted(Options);
        return db.MemberInvites.AsNoTracking().OrderBy(invite => invite.Id).ToList();
    }

    /// <summary>
    /// Runs the operations released at the same instant, and returns the failure each one produced
    /// (<see langword="null"/> for the one that succeeded), in the order they were given.
    ///
    /// The barrier is what opens the window the rules have to close: without it the operations
    /// would almost certainly run one after the other, and the test would pass against code with no
    /// serialisation at all.
    /// </summary>
    public static async Task<Exception?[]> RunAsync(params Func<CancellationToken, Task>[] operations)
    {
        ArgumentNullException.ThrowIfNull(operations);

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

    /// <summary>
    /// Asserts that exactly one of the attempts succeeded, and returns the one failure - which the
    /// test then checks the message of. Both halves matter: "one succeeded" without "one failed"
    /// would pass if both had.
    /// </summary>
    public static Exception SingleFailure(Exception?[] failures)
    {
        ArgumentNullException.ThrowIfNull(failures);

        Assert.Single(failures.Where(failure => failure is null));
        return Assert.Single(failures.Where(failure => failure is not null))!;
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        return ValueTask.CompletedTask;
    }
}
