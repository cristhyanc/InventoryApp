using Inventory.Application.PlatformNayaxSqs;
using Inventory.Domain.Exceptions;
using Inventory.Domain.NayaxSqs;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IPlatformNayaxSqsSenderStore"/> (issue #1101, under the
/// owner's approvals of 5 and 6 November 2026). Like every other adapter in this folder it lives
/// beside the <see cref="AppDbContext"/> and the persistence models it reads and writes.
///
/// <para><strong>Three tables, and the whole of what this surface may reach.</strong> It reads the
/// <c>Businesses</c> row the decision names - structurally non-tenant-owned, so no filter applies to
/// it - projecting its key and its lifecycle status and nothing else, and reads and writes
/// <c>BusinessNayaxSqsSenders</c> and <c>BusinessNayaxSqsSenderAudits</c> for that one business. No
/// Nayax credential, sale, stock, cost, purchase, product, machine, site, document or plan record is
/// readable or writable here, and no method takes a predicate, a column list, a page, a sort or a
/// sender id to look up <em>by</em>.</para>
///
/// <para><strong>The bypass, and why it is this narrow.</strong> The request it serves is the
/// platform administrator's, who is a member of no business, so it carries a <em>denied</em>
/// <c>BusinessScope</c>: the central filter
/// <c>!TenantFilteringEnabled || e.BusinessId == CurrentBusinessId</c> matches none of a business's
/// sender rows, and <c>BusinessOwnershipEnforcer</c> refuses every tenant-owned write. This adapter
/// therefore does two things explicitly, and nothing more. Its statements over
/// <c>BusinessNayaxSqsSenders</c> use <c>IgnoreQueryFilters</c>, each pinned to the single business id
/// the decision named, projecting that row's sender id, enabled flag, last-message instant, revision
/// and instants - this is the <strong>seventh</strong> reviewed <c>IgnoreQueryFilters</c> path in a
/// request (AGENTS.md § There are exactly seven reviewed IgnoreQueryFilters paths), and the
/// alternatives were the ones issue #544 already rejected: an unscoped context in a request path
/// would hand one adapter every tenant-owned table at once, and resolving each business into the
/// request scope would make a platform decision impersonate a tenant. And the write takes
/// <c>AppDbContext.BeginNayaxSqsSenderWrite</c> around its single <c>SaveChanges</c>, which permits
/// exactly one business's sender row and the audit row beside it. A wider projection, a statement
/// over another tenant-owned table, a predicate on anything but that one business id, or a lease
/// taken anywhere else is a boundary change needing its own reviewed issue - not an implementation
/// detail.</para>
///
/// <para><strong>The audit table is read through the same bypass and never published.</strong> It is
/// read for one reason only - so a decision's audit row is written on a tracked, filtered-out table
/// through <c>SaveChanges</c> - and nothing here projects one back out: the trail is evidence in the
/// database, not a response.</para>
///
/// <para><strong>The row and its audit row are one save.</strong> They are written in the same
/// <c>SaveChanges</c>, inside the caller's <c>BEGIN IMMEDIATE</c> transaction, so there is no moment
/// at which a database says whose sales may be attributed to an IAM user with no record of who said
/// so, or holds a record of a decision that was rolled back.</para>
///
/// <para><strong>Nothing here deletes a row.</strong> Removing a sender is an update that clears the
/// id and switches the mapping off; the lease refuses a delete of either table even while it is
/// open.</para>
/// </summary>
public sealed class EfPlatformNayaxSqsSenderStore : IPlatformNayaxSqsSenderStore
{
    /// <summary>SQLITE_CONSTRAINT, the primary result code a unique-index violation reports.</summary>
    private const int SqliteConstraintViolation = 19;

    /// <summary>
    /// The column the global unique index is over, as it appears in SQLite's own violation message.
    /// Matching on it is what keeps the translation below about <em>this</em> constraint rather than
    /// about any failed save.
    /// </summary>
    private const string SenderIdIndexColumn = "AwsSenderId";

    private readonly AppDbContext _db;

    public EfPlatformNayaxSqsSenderStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<PlatformNayaxSqsSenderTarget?> FindAsync(
        int businessId,
        CancellationToken cancellationToken)
    {
        // The business row first, and not as proof of tenancy: it is what makes the route value the
        // subject of a decision rather than something a request chose, and an id matching no row is
        // simply not found. Two columns, and the status is the one the lifecycle gate is decided on.
        var business = await _db.Businesses
            .AsNoTracking()
            .Where(candidate => candidate.Id == businessId)
            .Select(candidate => new { candidate.Id, candidate.Status })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (business is null)
        {
            return null;
        }

        var sender = await SendersOf(businessId)
            .AsNoTracking()
            .Select(row => new PlatformNayaxSqsSenderState(
                row.AwsSenderId,
                row.IsEnabled,
                row.LastMessageReceivedAtUtc,
                row.Revision,
                row.CreatedAtUtc,
                row.UpdatedAtUtc))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PlatformNayaxSqsSenderTarget(business.Id, business.Status, sender);
    }

    /// <inheritdoc />
    public async Task<IPlatformNayaxSqsSenderWriteTransaction> BeginWriteTransactionAsync(
        CancellationToken cancellationToken) =>
        new WriteTransaction(
            await SqliteWriteLockTransaction.BeginAsync(_db, cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public async Task SaveAsync(
        PlatformNayaxSqsSenderWrite write,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);

        // Tracked, because the update below is made on this very row and goes through SaveChanges -
        // where BusinessOwnershipEnforcer and every other central enforcer runs. A set-based
        // ExecuteUpdate would write straight past all of them.
        var sender = await SendersOf(write.BusinessId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (sender is null)
        {
            _db.BusinessNayaxSqsSenders.Add(new BusinessNayaxSqsSender
            {
                AwsSenderId = write.AwsSenderId,
                IsEnabled = write.IsEnabled,
                Revision = write.Revision,
                CreatedAtUtc = write.OccurredAtUtc,
                UpdatedAtUtc = write.OccurredAtUtc,

                // BusinessId is deliberately left unset: the lease below stamps it, through the same
                // central enforcer every ordinary insert goes through, so this adapter never assigns
                // an owner itself. LastMessageReceivedAtUtc is left null, because no message has
                // arrived - that column is issue #1103's to write.
            });
        }
        else
        {
            sender.AwsSenderId = write.AwsSenderId;
            sender.IsEnabled = write.IsEnabled;
            sender.Revision = write.Revision;
            sender.UpdatedAtUtc = write.OccurredAtUtc;
        }

        _db.BusinessNayaxSqsSenderAudits.Add(new BusinessNayaxSqsSenderAudit
        {
            FromAwsSenderId = write.FromAwsSenderId,
            ToAwsSenderId = write.AwsSenderId,
            FromIsEnabled = write.FromIsEnabled,
            ToIsEnabled = write.IsEnabled,
            Revision = write.Revision,
            ActorDirectoryTenantId = write.Actor.DirectoryTenantId,
            ActorObjectId = write.Actor.ObjectId,
            OccurredAtUtc = write.OccurredAtUtc,
        });

        try
        {
            // One save, one lease: the sender row and its audit row reach the database together or
            // not at all, and the window closes before this method returns.
            using (_db.BeginNayaxSqsSenderWrite(write.BusinessId))
            {
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (DbUpdateException failure) when (IsDuplicateSenderId(failure))
        {
            // The index's own answer, reported as this surface's documented 409 rather than a 500
            // that names a database object. It is the only way "another business already holds this
            // id" is detected, which is what keeps it from depending on reading that business's row,
            // and the message names no business at all.
            throw new DomainConflictException(NayaxSqsSenderPolicy.DuplicateSenderIdMessage);
        }
    }

    /// <summary>
    /// That one business's sender row, and the one place the filter bypass is written.
    ///
    /// <para>Both callers read through here, so "pinned to the business id the route named" is one
    /// statement rather than two that could drift. There is no overload taking a predicate, a page or
    /// a sender id: the business id is the only thing that narrows it, and the unique index on
    /// <c>BusinessId</c> is what makes <c>SingleOrDefault</c> the right shape.</para>
    /// </summary>
    private IQueryable<BusinessNayaxSqsSender> SendersOf(int businessId) =>
        _db.BusinessNayaxSqsSenders
            .IgnoreQueryFilters()
            .Where(row => row.BusinessId == businessId);

    private static bool IsDuplicateSenderId(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqlite
                && sqlite.SqliteErrorCode == SqliteConstraintViolation
                && sqlite.Message.Contains(SenderIdIndexColumn, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The caller's transaction, over the shared <see cref="SqliteWriteLockTransaction"/> every write
    /// path in this folder opens: <c>BEGIN IMMEDIATE</c>, so the write lock is held before the read
    /// the revision comparison is made on.
    /// </summary>
    private sealed class WriteTransaction : IPlatformNayaxSqsSenderWriteTransaction
    {
        private readonly SqliteWriteLockTransaction _transaction;

        public WriteTransaction(SqliteWriteLockTransaction transaction)
        {
            _transaction = transaction;
        }

        public Task CommitAsync(CancellationToken cancellationToken) =>
            _transaction.CommitAsync(cancellationToken);

        public ValueTask DisposeAsync() => _transaction.DisposeAsync();
    }
}
