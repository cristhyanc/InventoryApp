using Inventory.Application.PlatformNayaxSqs;
using Inventory.Domain.Exceptions;
using Inventory.Domain.NayaxSqs;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The Nayax SQS sender adapter of issue #1101 against relational SQLite with the real migration
/// history: what a platform decision may read and write under a <em>denied</em> business scope, and
/// everything the same context still refuses.
///
/// <para>Relational rather than a model double, for the reason the other store tests in this folder
/// are: the write lease, the central ownership stamp, the tenant query filters, the global unique
/// index, the check constraint and "the sender row and its audit row commit together or not at all"
/// are properties of a real database and a real <c>SaveChanges</c>, not of a fake.</para>
///
/// <para><strong>Two businesses throughout</strong> (AGENTS.md § Tenant ownership and data
/// isolation): every decision names one of them, so a write reaching the other would fail a test
/// rather than be invisible.</para>
/// </summary>
public class EfPlatformNayaxSqsSenderStoreTests : IAsyncLifetime
{
    private const int FirstBusinessId = 1;
    private const int SecondBusinessId = 2;
    private const string DirectoryTenantId = "99999999-9999-9999-9999-999999999999";
    private const string AdministratorObjectId = "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA";
    private const string SenderId = "AIDAEXAMPLEUSERID123";
    private const string OtherSenderId = "AIDAEXAMPLEUSERID456";

    private static readonly DateTime Now = new(2026, 11, 6, 3, 45, 0, DateTimeKind.Utc);

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

    #region The business-pinned read

    [Fact]
    public async Task An_unknown_business_is_not_found()
    {
        await using var db = TestAppDbContext.Denied(_options);

        Assert.Null(await new EfPlatformNayaxSqsSenderStore(db).FindAsync(404, CancellationToken.None));
    }

    /// <summary>
    /// A business with no recorded sender is found with its status and no sender - which is what
    /// makes "no such business" (<c>null</c>) a different answer from "nothing recorded yet".
    /// </summary>
    [Fact]
    public async Task A_business_with_no_recorded_sender_reads_as_holding_none()
    {
        await using var db = TestAppDbContext.Denied(_options);

        var target = await new EfPlatformNayaxSqsSenderStore(db)
            .FindAsync(FirstBusinessId, CancellationToken.None);

        Assert.NotNull(target);
        Assert.Equal(FirstBusinessId, target.BusinessId);
        Assert.Equal(BusinessStatus.Active, target.Status);
        Assert.Null(target.Sender);
    }

    /// <summary>
    /// The projection, read back whole: the sender id, the flag, the last-message instant the queue
    /// reader will write, the revision and both instants - each marked UTC, which the SQLite provider
    /// does not do by itself.
    /// </summary>
    [Fact]
    public async Task The_read_projects_the_senders_own_columns_marked_utc()
    {
        await SeedSenderAsync(FirstBusinessId, SenderId, isEnabled: true, revision: 4);

        await using var db = TestAppDbContext.Denied(_options);
        var target = await new EfPlatformNayaxSqsSenderStore(db)
            .FindAsync(FirstBusinessId, CancellationToken.None);

        var sender = target!.Sender;
        Assert.NotNull(sender);
        Assert.Equal(SenderId, sender.AwsSenderId);
        Assert.True(sender.IsEnabled);
        Assert.Equal(Now.AddDays(-1), sender.LastMessageReceivedAtUtc);
        Assert.Equal(DateTimeKind.Utc, sender.LastMessageReceivedAtUtc!.Value.Kind);
        Assert.Equal(4, sender.Revision);
        Assert.Equal(Now.AddDays(-7), sender.CreatedAtUtc);
        Assert.Equal(DateTimeKind.Utc, sender.CreatedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, sender.UpdatedAtUtc.Kind);
    }

    /// <summary>
    /// The read is pinned to the business the route named: the other business's row is never
    /// returned, even though the denied scope's filter matches neither.
    /// </summary>
    [Fact]
    public async Task The_read_returns_only_the_business_it_names()
    {
        await SeedSenderAsync(FirstBusinessId, SenderId, isEnabled: true, revision: 1);
        await SeedSenderAsync(SecondBusinessId, OtherSenderId, isEnabled: false, revision: 2);

        await using var db = TestAppDbContext.Denied(_options);
        var store = new EfPlatformNayaxSqsSenderStore(db);

        var first = await store.FindAsync(FirstBusinessId, CancellationToken.None);
        var second = await store.FindAsync(SecondBusinessId, CancellationToken.None);

        Assert.Equal(SenderId, first!.Sender!.AwsSenderId);
        Assert.Equal(OtherSenderId, second!.Sender!.AwsSenderId);
    }

    /// <summary>
    /// The ordinary read is still filtered to nothing under a denied scope, which is what makes this
    /// adapter's explicit statements the whole of the exception rather than one way of several.
    /// </summary>
    [Fact]
    public async Task The_ordinary_read_of_the_table_still_matches_nothing_under_a_denied_scope()
    {
        await SeedSenderAsync(FirstBusinessId, SenderId, isEnabled: true, revision: 1);

        await using var db = TestAppDbContext.Denied(_options);

        Assert.Empty(await db.BusinessNayaxSqsSenders.AsNoTracking().ToListAsync());
        Assert.Empty(await db.BusinessNayaxSqsSenderAudits.AsNoTracking().ToListAsync());

        // And nothing else tenant-owned became readable either.
        Assert.Empty(await db.Products.AsNoTracking().ToListAsync());
        Assert.Empty(await db.Suppliers.AsNoTracking().ToListAsync());
    }

    #endregion

    #region The write, and what it writes

    /// <summary>
    /// The write, end to end on one context: the sender row and its audit row arrive together, the
    /// business key is stamped by the central enforcer rather than assigned by the adapter, and the
    /// instants come back marked UTC.
    /// </summary>
    [Fact]
    public async Task A_first_decision_writes_the_sender_and_its_audit_row_together()
    {
        await WriteAsync(Write(FirstBusinessId, SenderId, isEnabled: true, revision: 1));

        await using var reader = TestAppDbContext.Unrestricted(_options);

        var sender = await reader.BusinessNayaxSqsSenders.AsNoTracking().SingleAsync();
        Assert.Equal(FirstBusinessId, sender.BusinessId);
        Assert.Equal(SenderId, sender.AwsSenderId);
        Assert.True(sender.IsEnabled);
        Assert.Null(sender.LastMessageReceivedAtUtc);
        Assert.Equal(1, sender.Revision);
        Assert.Equal(Now, sender.CreatedAtUtc);
        Assert.Equal(DateTimeKind.Utc, sender.CreatedAtUtc.Kind);
        Assert.Equal(Now, sender.UpdatedAtUtc);

        var audit = await reader.BusinessNayaxSqsSenderAudits.AsNoTracking().SingleAsync();
        Assert.Equal(FirstBusinessId, audit.BusinessId);
        Assert.Null(audit.FromAwsSenderId);
        Assert.Equal(SenderId, audit.ToAwsSenderId);
        Assert.False(audit.FromIsEnabled);
        Assert.True(audit.ToIsEnabled);
        Assert.Equal(1, audit.Revision);
        Assert.Equal(DirectoryTenantId, audit.ActorDirectoryTenantId);
        Assert.Equal(AdministratorObjectId, audit.ActorObjectId);
        Assert.Equal(Now, audit.OccurredAtUtc);
        Assert.Equal(DateTimeKind.Utc, audit.OccurredAtUtc.Kind);
    }

    /// <summary>
    /// "Committed together or not at all", read from the other direction: a transaction that is never
    /// committed leaves neither row behind.
    /// </summary>
    [Fact]
    public async Task A_decision_that_is_not_committed_leaves_neither_row()
    {
        await using (var db = TestAppDbContext.Denied(_options))
        {
            var store = new EfPlatformNayaxSqsSenderStore(db);
            await using var transaction = await store.BeginWriteTransactionAsync(CancellationToken.None);

            await store.SaveAsync(
                Write(FirstBusinessId, SenderId, isEnabled: true, revision: 1), CancellationToken.None);

            // Disposed without committing, which is what a refused rule does to a decision.
        }

        await using var reader = TestAppDbContext.Unrestricted(_options);
        Assert.Empty(await reader.BusinessNayaxSqsSenders.AsNoTracking().ToListAsync());
        Assert.Empty(await reader.BusinessNayaxSqsSenderAudits.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// A second decision updates the one row and appends a second audit row: the trail grows, the
    /// mapping does not multiply, and the row keeps the creation instant the first decision wrote.
    /// </summary>
    [Fact]
    public async Task A_second_decision_updates_the_row_and_appends_to_the_trail()
    {
        await WriteAsync(Write(FirstBusinessId, SenderId, isEnabled: true, revision: 1));
        await WriteAsync(Write(FirstBusinessId, OtherSenderId, isEnabled: false, revision: 2) with
        {
            FromAwsSenderId = SenderId,
            FromIsEnabled = true,
            OccurredAtUtc = Now.AddMinutes(5),
        });

        await using var reader = TestAppDbContext.Unrestricted(_options);

        var sender = await reader.BusinessNayaxSqsSenders.AsNoTracking().SingleAsync();
        Assert.Equal(OtherSenderId, sender.AwsSenderId);
        Assert.False(sender.IsEnabled);
        Assert.Equal(2, sender.Revision);
        Assert.Equal(Now, sender.CreatedAtUtc);
        Assert.Equal(Now.AddMinutes(5), sender.UpdatedAtUtc);

        var trail = await reader.BusinessNayaxSqsSenderAudits
            .AsNoTracking()
            .OrderBy(row => row.Id)
            .ToListAsync();
        Assert.Equal(2, trail.Count);
        Assert.Null(trail[0].FromAwsSenderId);
        Assert.Equal(SenderId, trail[1].FromAwsSenderId);
        Assert.Equal(OtherSenderId, trail[1].ToAwsSenderId);
    }

    /// <summary>
    /// The two-business criterion: a decision about one business leaves the other's row exactly as it
    /// was, revision included.
    /// </summary>
    [Fact]
    public async Task A_decision_reaches_only_the_business_it_names()
    {
        await WriteAsync(Write(SecondBusinessId, OtherSenderId, isEnabled: true, revision: 1));
        await WriteAsync(Write(FirstBusinessId, SenderId, isEnabled: true, revision: 1));

        await using var reader = TestAppDbContext.Unrestricted(_options);
        var rows = await reader.BusinessNayaxSqsSenders
            .AsNoTracking()
            .OrderBy(row => row.BusinessId)
            .ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.Equal(SenderId, rows[0].AwsSenderId);
        Assert.Equal(OtherSenderId, rows[1].AwsSenderId);
        Assert.All(rows, row => Assert.Equal(1, row.Revision));
    }

    #endregion

    #region The global unique index

    /// <summary>
    /// The whole point of the index: the sender id another business holds is refused by the database,
    /// reported as this surface's documented conflict, and the refusal names no business.
    /// </summary>
    [Fact]
    public async Task A_sender_id_another_business_holds_is_refused_by_the_index()
    {
        await WriteAsync(Write(SecondBusinessId, SenderId, isEnabled: true, revision: 1));

        await using (var db = TestAppDbContext.Denied(_options))
        {
            var store = new EfPlatformNayaxSqsSenderStore(db);
            await using var transaction = await store.BeginWriteTransactionAsync(CancellationToken.None);

            var refusal = await Assert.ThrowsAsync<DomainConflictException>(() => store.SaveAsync(
                Write(FirstBusinessId, SenderId, isEnabled: true, revision: 1), CancellationToken.None));

            Assert.Equal(NayaxSqsSenderPolicy.DuplicateSenderIdMessage, refusal.Message);
            Assert.DoesNotContain("Second Vending", refusal.Message, StringComparison.Ordinal);
        }

        // Nothing landed for the business that asked.
        await using var reader = TestAppDbContext.Unrestricted(_options);
        var rows = await reader.BusinessNayaxSqsSenders.AsNoTracking().ToListAsync();
        Assert.Equal(SecondBusinessId, Assert.Single(rows).BusinessId);
        Assert.Single(await reader.BusinessNayaxSqsSenderAudits.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// And the filter on that index is what makes a removal useful: once one business clears its
    /// sender id, another may record it - and two cleared rows do not collide on <c>NULL</c>.
    /// </summary>
    [Fact]
    public async Task A_removed_sender_id_is_free_for_another_business()
    {
        await WriteAsync(Write(SecondBusinessId, SenderId, isEnabled: true, revision: 1));

        // Remove: an update that clears the id and switches the mapping off.
        await WriteAsync(Write(SecondBusinessId, awsSenderId: null, isEnabled: false, revision: 2) with
        {
            FromAwsSenderId = SenderId,
            FromIsEnabled = true,
        });

        // And the first business may now record it, which the index allows because the other row's
        // value is null.
        await WriteAsync(Write(FirstBusinessId, SenderId, isEnabled: true, revision: 1));

        await using var reader = TestAppDbContext.Unrestricted(_options);
        var rows = await reader.BusinessNayaxSqsSenders
            .AsNoTracking()
            .OrderBy(row => row.BusinessId)
            .ToListAsync();

        Assert.Equal(SenderId, rows[0].AwsSenderId);
        Assert.Equal(FirstBusinessId, rows[0].BusinessId);
        // The row that gave it up is still there, with its history and its new revision.
        Assert.Null(rows[1].AwsSenderId);
        Assert.Equal(2, rows[1].Revision);
    }

    /// <summary>
    /// The check constraint, as the last line of defence behind the Domain rule: a row switched on
    /// with no sender id cannot exist in the database at all, whatever wrote it.
    /// </summary>
    [Fact]
    public async Task An_enabled_row_with_no_sender_id_is_refused_by_the_database()
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        db.BusinessNayaxSqsSenders.Add(new BusinessNayaxSqsSender
        {
            BusinessId = FirstBusinessId,
            AwsSenderId = null,
            IsEnabled = true,
            Revision = 1,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    #endregion

    #region The lease

    /// <summary>
    /// The lease is as narrow as it claims: while it is open, a write of any other tenant-owned
    /// entity is refused by <c>BusinessOwnershipEnforcer</c> exactly as it is without one, so the
    /// decision cannot carry an unrelated row into the database with it.
    /// </summary>
    [Fact]
    public async Task Another_tenant_owned_row_cannot_ride_along_with_a_decision()
    {
        await using var db = TestAppDbContext.Denied(_options);
        var store = new EfPlatformNayaxSqsSenderStore(db);
        await using var transaction = await store.BeginWriteTransactionAsync(CancellationToken.None);

        db.Suppliers.Add(new Supplier { Name = "Smuggled supplier", BusinessId = FirstBusinessId });

        await Assert.ThrowsAsync<CrossBusinessAccessException>(() => store.SaveAsync(
            Write(FirstBusinessId, SenderId, isEnabled: true, revision: 1), CancellationToken.None));
    }

    /// <summary>
    /// And it grants nothing to another business: a sender row carrying a different business's key is
    /// refused by the same central check every ordinary insert goes through, rather than being
    /// silently re-owned.
    /// </summary>
    [Fact]
    public async Task A_sender_row_of_another_business_is_refused_while_a_lease_is_open()
    {
        await using var db = TestAppDbContext.Denied(_options);
        var store = new EfPlatformNayaxSqsSenderStore(db);
        await using var transaction = await store.BeginWriteTransactionAsync(CancellationToken.None);

        db.BusinessNayaxSqsSenders.Add(new BusinessNayaxSqsSender
        {
            BusinessId = SecondBusinessId,
            AwsSenderId = OtherSenderId,
            IsEnabled = true,
            Revision = 1,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        });

        await Assert.ThrowsAsync<CrossBusinessAccessException>(() => store.SaveAsync(
            Write(FirstBusinessId, SenderId, isEnabled: true, revision: 1), CancellationToken.None));
    }

    /// <summary>
    /// A delete of either table is refused while the lease is open, which is the rule that makes
    /// "Remove is an update" structural rather than a convention: there is no route that deletes one,
    /// and the lease would refuse it if there were.
    /// </summary>
    [Fact]
    public async Task A_delete_of_either_table_is_refused_while_a_lease_is_open()
    {
        await WriteAsync(Write(FirstBusinessId, SenderId, isEnabled: true, revision: 1));

        await using (var db = TestAppDbContext.Denied(_options))
        {
            var store = new EfPlatformNayaxSqsSenderStore(db);
            await using var transaction = await store.BeginWriteTransactionAsync(CancellationToken.None);

            var sender = await db.BusinessNayaxSqsSenders.IgnoreQueryFilters().SingleAsync();
            db.BusinessNayaxSqsSenders.Remove(sender);

            await Assert.ThrowsAsync<CrossBusinessAccessException>(() =>
                db.SaveChangesAsync(CancellationToken.None));
        }

        await using (var db = TestAppDbContext.Denied(_options))
        {
            var store = new EfPlatformNayaxSqsSenderStore(db);
            await using var transaction = await store.BeginWriteTransactionAsync(CancellationToken.None);

            var audit = await db.BusinessNayaxSqsSenderAudits.IgnoreQueryFilters().SingleAsync();
            db.BusinessNayaxSqsSenderAudits.Remove(audit);

            await Assert.ThrowsAsync<CrossBusinessAccessException>(() =>
                db.SaveChangesAsync(CancellationToken.None));
        }

        // Both rows survived both attempts.
        await using var reader = TestAppDbContext.Unrestricted(_options);
        Assert.Single(await reader.BusinessNayaxSqsSenders.AsNoTracking().ToListAsync());
        Assert.Single(await reader.BusinessNayaxSqsSenderAudits.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// The audit trail is append-only in practice as well as by intent: there is no path that updates
    /// one, and an update attempted directly under the lease is refused.
    /// </summary>
    [Fact]
    public async Task An_audit_row_cannot_be_changed_under_the_lease()
    {
        await WriteAsync(Write(FirstBusinessId, SenderId, isEnabled: true, revision: 1));

        await using var db = TestAppDbContext.Denied(_options);
        var store = new EfPlatformNayaxSqsSenderStore(db);
        await using var transaction = await store.BeginWriteTransactionAsync(CancellationToken.None);

        var audit = await db.BusinessNayaxSqsSenderAudits.IgnoreQueryFilters().SingleAsync();
        audit.ToAwsSenderId = "AIDAREWRITTENIDXXXXX";

        await Assert.ThrowsAsync<CrossBusinessAccessException>(() => store.SaveAsync(
            Write(FirstBusinessId, OtherSenderId, isEnabled: true, revision: 2), CancellationToken.None));
    }

    /// <summary>
    /// Outside a decision the denied scope is unchanged: a sender row written without the lease is
    /// refused, so the lease is the only way this table is reachable from a platform request.
    /// </summary>
    [Fact]
    public async Task Without_a_lease_a_denied_scope_still_cannot_write_a_sender()
    {
        await using var db = TestAppDbContext.Denied(_options);

        db.BusinessNayaxSqsSenders.Add(new BusinessNayaxSqsSender
        {
            BusinessId = FirstBusinessId,
            AwsSenderId = SenderId,
            IsEnabled = true,
            Revision = 1,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        });

        await Assert.ThrowsAsync<CrossBusinessAccessException>(() => db.SaveChangesAsync());
    }

    /// <summary>
    /// A lease may be taken only by a request that resolved no business of its own: a member's scope
    /// and a support session's cannot open one at all, so this path is unreachable from inside a
    /// business.
    /// </summary>
    [Fact]
    public async Task A_resolved_or_support_session_scope_cannot_write_through_this_adapter()
    {
        await using (var member = TestAppDbContext.For(_options, FirstBusinessId))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new EfPlatformNayaxSqsSenderStore(member).SaveAsync(
                    Write(FirstBusinessId, SenderId, isEnabled: true, revision: 1), CancellationToken.None));
        }

        await using (var support = TestAppDbContext.SupportSession(_options, FirstBusinessId))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new EfPlatformNayaxSqsSenderStore(support).SaveAsync(
                    Write(FirstBusinessId, SenderId, isEnabled: true, revision: 1), CancellationToken.None));
        }

        await using var reader = TestAppDbContext.Unrestricted(_options);
        Assert.Empty(await reader.BusinessNayaxSqsSenders.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// One lease at a time, and never beside another: a second decision on the same context while one
    /// is open is refused rather than quietly widening the window either of them opened.
    /// </summary>
    [Fact]
    public async Task A_second_lease_cannot_be_opened_beside_this_one()
    {
        await using var db = TestAppDbContext.Denied(_options);
        var store = new EfPlatformNayaxSqsSenderStore(db);
        await using var transaction = await store.BeginWriteTransactionAsync(CancellationToken.None);

        // The store opens its lease inside SaveAsync, so holding one open here means driving the
        // context's own lease API, which is what the adapter does.
        using var lease = OpenSenderLease(db, FirstBusinessId);

        Assert.Throws<InvalidOperationException>(() => OpenSenderLease(db, FirstBusinessId));
        Assert.Throws<InvalidOperationException>(() => OpenPlanGrantLease(db, FirstBusinessId));
        Assert.Throws<InvalidOperationException>(() => OpenSupplierProvisioningLease(db, FirstBusinessId));
    }

    #endregion

    /// <summary>
    /// What the platform writes is an ordinary tenant-owned row: the business's own scope reads it
    /// through the central filters with nothing added, and the other business's scope sees none of
    /// it. This is the claim that makes the table tenant-owned rather than a platform table.
    /// </summary>
    [Fact]
    public async Task What_the_platform_writes_is_read_by_that_business_through_the_central_filters()
    {
        await WriteAsync(Write(FirstBusinessId, SenderId, isEnabled: true, revision: 1));

        await using (var owner = TestAppDbContext.For(_options, FirstBusinessId))
        {
            var row = Assert.Single(await owner.BusinessNayaxSqsSenders.AsNoTracking().ToListAsync());
            Assert.Equal(SenderId, row.AwsSenderId);
            Assert.Single(await owner.BusinessNayaxSqsSenderAudits.AsNoTracking().ToListAsync());
        }

        await using var other = TestAppDbContext.For(_options, SecondBusinessId);
        Assert.Empty(await other.BusinessNayaxSqsSenders.AsNoTracking().ToListAsync());
        Assert.Empty(await other.BusinessNayaxSqsSenderAudits.AsNoTracking().ToListAsync());
    }

    private static IDisposable OpenSenderLease(AppDbContext db, int businessId) =>
        InvokeLease(db, "BeginNayaxSqsSenderWrite", businessId);

    private static IDisposable OpenPlanGrantLease(AppDbContext db, int businessId) =>
        InvokeLease(db, "BeginPlanGrantWrite", businessId);

    private static IDisposable OpenSupplierProvisioningLease(AppDbContext db, int businessId) =>
        InvokeLease(db, "BeginSupplierProvisioning", businessId);

    /// <summary>
    /// The leases are <c>internal</c> to <c>Inventory.Infrastructure</c> - which is the point, since
    /// only that assembly's adapters may take one - so this test reaches them reflectively rather
    /// than by widening their visibility for a test's benefit.
    /// </summary>
    private static IDisposable InvokeLease(AppDbContext db, string method, int businessId)
    {
        var lease = typeof(AppDbContext).GetMethod(
            method,
            System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public);

        Assert.NotNull(lease);

        try
        {
            return (IDisposable)lease.Invoke(db, [businessId])!;
        }
        catch (System.Reflection.TargetInvocationException failure) when (failure.InnerException is not null)
        {
            throw failure.InnerException;
        }
    }

    private async Task WriteAsync(PlatformNayaxSqsSenderWrite write)
    {
        await using var db = TestAppDbContext.Denied(_options);
        var store = new EfPlatformNayaxSqsSenderStore(db);
        await using var transaction = await store.BeginWriteTransactionAsync(CancellationToken.None);

        await store.SaveAsync(write, CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
    }

    private async Task SeedSenderAsync(int businessId, string? senderId, bool isEnabled, int revision)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        db.BusinessNayaxSqsSenders.Add(new BusinessNayaxSqsSender
        {
            BusinessId = businessId,
            AwsSenderId = senderId,
            IsEnabled = isEnabled,
            LastMessageReceivedAtUtc = Now.AddDays(-1),
            Revision = revision,
            CreatedAtUtc = Now.AddDays(-7),
            UpdatedAtUtc = Now.AddDays(-2),
        });

        await db.SaveChangesAsync();
    }

    private static PlatformNayaxSqsSenderWrite Write(
        int businessId,
        string? awsSenderId,
        bool isEnabled,
        int revision)
    {
        Assert.True(ActorIdentity.TryCreate(DirectoryTenantId, AdministratorObjectId, out var actor));

        return new PlatformNayaxSqsSenderWrite(
            businessId,
            awsSenderId,
            isEnabled,
            revision,
            FromAwsSenderId: null,
            FromIsEnabled: false,
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
