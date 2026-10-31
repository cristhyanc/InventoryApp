using Inventory.Application.Attachments;
using Inventory.Application.Documents;
using Inventory.Application.Time;
using Inventory.Domain.Attachments;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IAttachmentQuotaStore"/> (issue #968): the current
/// business's attachment storage usage, and the reservation that makes an upload safe.
///
/// <para><strong>Usage is derived, never a counter.</strong> It is summed from the rows that
/// actually refer to an attachment plus the unexpired reservations, so deleting a purchase, an
/// expense or an attachment releases its bytes the moment that change commits - with nothing to
/// decrement, nothing to drift and nothing to repair if a decrement were ever missed. A physical
/// file no row refers to is an orphan and is deliberately not counted; the reconciliation command
/// reports it.</para>
///
/// <para><strong>Reserving is one serialised operation.</strong> The read of the usage, the
/// comparison with the allowance and the reservation's insert all happen inside one
/// <see cref="SqliteWriteLockTransaction"/>, so the second of two concurrent uploads waits, reads
/// the first one's committed reservation and is refused rather than passing a check only one of
/// them fits through. A deferred transaction would take the write lock after the read the decision
/// rests on, which is the race this exists to remove.</para>
///
/// <para>Every statement here runs through the ordinary tenant query filters and the central
/// ownership stamp: no business id is written, read or accepted anywhere, and a reservation of
/// another business is simply not there.</para>
/// </summary>
public sealed class EfAttachmentQuotaStore : IAttachmentQuotaStore
{
    private readonly AppDbContext _db;
    private readonly IClock _clock;

    public EfAttachmentQuotaStore(AppDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<AttachmentStorageUsage> ReadUsageAsync(CancellationToken cancellationToken = default) =>
        await ReadUsageAsync(_clock.UtcNow, excludedReservationId: null, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<AttachmentUploadReservation?> ReserveAsync(
        AttachmentUploadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var transaction = await SqliteWriteLockTransaction.BeginAsync(_db, cancellationToken);

        var now = _clock.UtcNow;

        // Housekeeping, inside the write lock and before the sum: an expired reservation already
        // counts for nothing, so removing it changes no answer - it only keeps the table from
        // growing with the uploads that were abandoned.
        await RemoveExpiredAsync(now, cancellationToken).ConfigureAwait(false);

        var current = await CurrentAttachmentAsync(request.Category, request.RecordId, cancellationToken)
            .ConfigureAwait(false);
        var chargeable = AttachmentStorageQuotaPolicy.ChargeableBytes(request.UploadBytes, current.SizeBytes);
        var usage = await ReadUsageAsync(now, excludedReservationId: null, cancellationToken).ConfigureAwait(false);

        if (!AttachmentStorageQuotaPolicy.Fits(usage.TotalBytes, chargeable, request.LimitBytes))
            return null;

        var entity = new AttachmentStorageReservation
        {
            Category = request.Category,
            RecordId = request.RecordId,
            UploadBytes = request.UploadBytes,
            ChargeableBytes = chargeable,
            LimitBytes = request.LimitBytes,
            RecordVersion = current.Version,
            CreatedAtUtc = now,
            ExpiresAtUtc = AttachmentStorageQuotaPolicy.ReservationExpiryFor(now),
        };

        _db.AttachmentStorageReservations.Add(entity);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new AttachmentUploadReservation(
            entity.Id,
            entity.Category,
            entity.RecordId,
            entity.UploadBytes,
            entity.ChargeableBytes,
            entity.LimitBytes,
            entity.RecordVersion,
            entity.ExpiresAtUtc);
    }

    /// <inheritdoc />
    public async Task<bool> ReleaseAsync(
        AttachmentUploadReservation reservation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reservation);

        var entity = await _db.AttachmentStorageReservations
            .FirstOrDefaultAsync(row => row.Id == reservation.Id, cancellationToken)
            .ConfigureAwait(false);
        if (entity is null) return false;

        _db.AttachmentStorageReservations.Remove(entity);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// The usage at <paramref name="nowUtc"/>, optionally ignoring one reservation - which is what
    /// a commit re-validating its own expired reservation needs, so the bytes it is about to charge
    /// are not counted twice.
    /// </summary>
    internal async Task<AttachmentStorageUsage> ReadUsageAsync(
        DateTime nowUtc, int? excludedReservationId, CancellationToken cancellationToken) =>
        await AttachmentStorageUsageQuery
            .ReadAsync(_db, nowUtc, excludedReservationId, cancellationToken)
            .ConfigureAwait(false);

    private async Task RemoveExpiredAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var expired = await _db.AttachmentStorageReservations
            .Where(row => row.ExpiresAtUtc <= nowUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (expired.Count == 0) return;

        _db.AttachmentStorageReservations.RemoveRange(expired);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// What the record this upload is for currently has: the size of the attachment being replaced
    /// and the version that size was read at, or nothing at all when there is no record, no
    /// attachment, or an attachment whose size was never recorded - which is credited as nothing
    /// rather than guessed at.
    /// </summary>
    private async Task<(long? SizeBytes, int? Version)> CurrentAttachmentAsync(
        DocumentCategory category, int? recordId, CancellationToken cancellationToken)
    {
        if (recordId is not { } id) return (null, null);

        switch (category)
        {
            case DocumentCategory.PurchaseDocument:
                // A purchase carries no attachment version: it is attach-only-if-empty (issue
                // #976), and that write makes its own decision with the write lock held.
                var purchase = await _db.Receipts
                    .AsNoTracking()
                    .Where(candidate => candidate.Id == id && candidate.StoredFileName != null)
                    .Select(candidate => new { candidate.FileSizeBytes })
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                return (purchase?.FileSizeBytes, null);

            case DocumentCategory.ExpenseAttachment:
                var expense = await _db.OperatingExpenses
                    .AsNoTracking()
                    .Where(candidate => candidate.Id == id)
                    .Select(candidate => new
                    {
                        candidate.AttachmentStoredFileName,
                        candidate.AttachmentFileSizeBytes,
                        candidate.AttachmentVersion,
                    })
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (expense is null) return (null, null);
                return (
                    expense.AttachmentStoredFileName is null ? null : expense.AttachmentFileSizeBytes,
                    expense.AttachmentVersion);

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(category), category, "Unknown attachment category.");
        }
    }
}
