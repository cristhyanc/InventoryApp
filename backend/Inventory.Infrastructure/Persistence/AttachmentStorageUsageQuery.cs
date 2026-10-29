using Inventory.Application.Attachments;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The one statement of what a business's attachments occupy (issue #968), shared by the two
/// places that have to agree about it: <see cref="EfAttachmentQuotaStore"/>, which reserves room
/// and reports the figures, and <see cref="AttachmentQuotaCommit"/>, which re-validates an expired
/// reservation inside the write transaction that is about to record an attachment.
///
/// <para>Writing it once is the point. An upload refused against one definition and reported
/// against another would be a quota nobody could reconcile, and the commit's re-validation has to
/// count exactly what the reservation counted - the stored bytes of the attachments the business's
/// active rows refer to, plus its unexpired reservations, and nothing else.</para>
///
/// <para>Both sums run through the ordinary tenant query filters, so they are the current
/// business's own rows with no predicate over a business anywhere.</para>
/// </summary>
internal static class AttachmentStorageUsageQuery
{
    /// <summary>
    /// Reads the usage at <paramref name="nowUtc"/>.
    /// </summary>
    /// <param name="db">The request's own context, inside whatever transaction the caller holds.</param>
    /// <param name="nowUtc">The instant reservations are judged expired against.</param>
    /// <param name="excludedReservationId">
    /// A reservation to leave out - the one a commit is consuming, whose bytes it is about to
    /// charge as the attachment's own size. Null counts every unexpired reservation.
    /// </param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public static async Task<AttachmentStorageUsage> ReadAsync(
        AppDbContext db, DateTime nowUtc, int? excludedReservationId, CancellationToken cancellationToken)
    {
        // A null size is an attachment nothing measured: it contributes nothing and is reported by
        // the reconciliation command, never estimated.
        var purchaseBytes = await db.Receipts
            .Where(purchase => purchase.StoredFileName != null)
            .SumAsync(purchase => (long?)purchase.FileSizeBytes, cancellationToken)
            .ConfigureAwait(false) ?? 0L;

        var expenseBytes = await db.OperatingExpenses
            .Where(expense => expense.AttachmentStoredFileName != null)
            .SumAsync(expense => (long?)expense.AttachmentFileSizeBytes, cancellationToken)
            .ConfigureAwait(false) ?? 0L;

        var reservedBytes = await db.AttachmentStorageReservations
            .Where(reservation => reservation.ExpiresAtUtc > nowUtc)
            .Where(reservation => excludedReservationId == null || reservation.Id != excludedReservationId)
            .SumAsync(reservation => (long?)reservation.ChargeableBytes, cancellationToken)
            .ConfigureAwait(false) ?? 0L;

        return new AttachmentStorageUsage(purchaseBytes + expenseBytes, reservedBytes);
    }
}
