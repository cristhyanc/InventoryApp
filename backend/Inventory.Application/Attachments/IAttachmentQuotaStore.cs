namespace Inventory.Application.Attachments;

/// <summary>
/// The Application's narrow port over the current business's attachment storage accounting
/// (issue #968).
///
/// <para>It takes no business identifier and no predicate: every statement it makes is scoped to
/// the business the request already resolved, through the ordinary tenant query filters and the
/// ownership stamp (AGENTS.md § Tenant ownership and data isolation). There is no method that
/// reads another business's usage, and no caller-supplied filter, column list or page anywhere on
/// it.</para>
///
/// <para><strong>Reserving is one serialised operation.</strong> The implementation computes the
/// usage, compares it with the allowance and writes the reservation inside one write transaction
/// with the write lock already held, so two concurrent uploads cannot both pass a check that only
/// one of them fits. Consuming a reservation is deliberately <em>not</em> here: it happens inside
/// the same transaction as the row that records the attachment, in the purchase and expense write
/// adapters, because that is the only way the reservation can become the attachment's usage
/// atomically.</para>
/// </summary>
public interface IAttachmentQuotaStore
{
    /// <summary>
    /// What the current business uses: the stored bytes of its attachments plus its unexpired
    /// reservations. Expired reservations are excluded by the read rather than by a sweep, so a
    /// crashed upload stops counting on its own.
    /// </summary>
    Task<AttachmentStorageUsage> ReadUsageAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reserves room for one upload, or refuses it.
    ///
    /// <para>Returns <see langword="null"/> when the upload does not fit. The chargeable figure is
    /// computed here, from the stored size of the attachment being replaced where there is one, so
    /// a request cannot declare what its upload costs.</para>
    /// </summary>
    Task<AttachmentUploadReservation?> ReserveAsync(
        AttachmentUploadRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases a reservation whose upload did not happen, and reports whether one was there to
    /// release. Idempotent: a reservation a commit already consumed, or one that has expired and
    /// been cleaned up, is simply not there.
    /// </summary>
    Task<bool> ReleaseAsync(
        AttachmentUploadReservation reservation, CancellationToken cancellationToken = default);
}
