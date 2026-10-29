using Inventory.Application.Attachments;
using Inventory.Application.Time;
using Inventory.Domain.Attachments;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// Turns an upload's reservation into the attachment's own usage, inside the write transaction
/// that records the attachment (issue #968).
///
/// <para><strong>Why it lives in the write adapter's transaction.</strong> The reservation and the
/// row have to stop and start counting together. Consumed in a transaction of its own, a crash
/// between the two would either charge a business twice for one file or charge it for nothing at
/// all; consumed here, the reservation row is deleted by the same commit that writes the
/// attachment's size, so the business's usage moves from one to the other atomically and a
/// rollback leaves both exactly as they were.</para>
///
/// <para><strong>An expired reservation is never committed on trust.</strong> If the reservation
/// has gone - expired and cleaned up, or already released - the quota is re-validated here, from
/// the upload's own size and the attachment's <em>current</em> size, against the allowance the
/// reservation was taken under. Still fits: the row commits normally. No longer fits: the write is
/// refused, nothing about the row changes, and the use case that catches the refusal removes the
/// file it had written.</para>
///
/// <para><strong>The version check is the same operation as the write.</strong> A replacement
/// reserved against version <em>n</em> commits only while the record is still at <em>n</em>, read
/// inside this transaction with the write lock held. Two replacements of one attachment therefore
/// cannot both succeed: the first moves the version on, and the second is refused with a conflict,
/// its file removed and its reservation released, so exactly one of them is ever charged.</para>
/// </summary>
internal static class AttachmentQuotaCommit
{
    /// <summary>
    /// Consumes <paramref name="reservation"/>, or refuses the write.
    ///
    /// <para>Call it inside the write transaction, before the attachment is recorded. It stages the
    /// reservation's removal on the context; the caller's own <c>SaveChanges</c> inside the same
    /// transaction persists it with the row.</para>
    /// </summary>
    /// <param name="db">The context the caller's transaction is open on.</param>
    /// <param name="clock">The application clock, for judging expiry.</param>
    /// <param name="reservation">The reservation the use case took before writing any bytes.</param>
    /// <param name="currentAttachmentBytes">
    /// The size of the attachment being replaced as this transaction reads it, or
    /// <see langword="null"/> when the record has none (or one of unrecorded size). Only used to
    /// re-validate an expired reservation.
    /// </param>
    /// <param name="currentRecordVersion">
    /// The record's attachment version as this transaction reads it, or <see langword="null"/> for
    /// a record that carries none.
    /// </param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <exception cref="AttachmentReplacementConflictException">
    /// The record's attachment moved on after the reservation was computed.
    /// </exception>
    /// <exception cref="AttachmentStorageQuotaExceededException">
    /// The reservation had expired and the upload no longer fits.
    /// </exception>
    public static async Task ConsumeAsync(
        AppDbContext db,
        IClock clock,
        AttachmentUploadReservation reservation,
        long? currentAttachmentBytes,
        int? currentRecordVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(reservation);

        // Checked first, and before anything is charged: a replacement that lost the race must not
        // have its storage accounted for at all.
        if (reservation.RecordVersion is { } reservedVersion && currentRecordVersion != reservedVersion)
            throw new AttachmentReplacementConflictException();

        var now = clock.UtcNow;
        var row = await db.AttachmentStorageReservations
            .FirstOrDefaultAsync(candidate => candidate.Id == reservation.Id, cancellationToken)
            .ConfigureAwait(false);

        var expired = row is null || AttachmentStorageQuotaPolicy.HasExpired(row.ExpiresAtUtc, now);
        if (expired)
        {
            var chargeable = AttachmentStorageQuotaPolicy.ChargeableBytes(
                reservation.UploadBytes, currentAttachmentBytes);
            var usage = await AttachmentStorageUsageQuery
                .ReadAsync(db, now, reservation.Id, cancellationToken)
                .ConfigureAwait(false);

            if (!AttachmentStorageQuotaPolicy.Fits(usage.TotalBytes, chargeable, reservation.LimitBytes))
                throw new AttachmentStorageQuotaExceededException();
        }

        // Staged, not saved: the caller's SaveChanges inside this same transaction is what makes
        // the reservation and the attachment's size change together.
        if (row is not null) db.AttachmentStorageReservations.Remove(row);
    }
}
