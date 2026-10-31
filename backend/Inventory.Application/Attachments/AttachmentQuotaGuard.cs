using Inventory.Application.Documents;
using Inventory.Application.Subscriptions;

namespace Inventory.Application.Attachments;

/// <summary>
/// The one place an upload path asks for room before it writes bytes (issue #968).
///
/// <para>It composes two things the upload use cases must not decide for themselves: what the
/// business's allowance is - the effective plan's <c>AttachmentStorageLimitBytes</c>, never a
/// number written here - and whether this upload fits, which is the reservation store's serialised
/// check. Every upload path (purchase create, attach-a-document-later, and the operating-expense
/// multipart create and update) goes through it, so a new path cannot quietly be the one that does
/// not check.</para>
///
/// <para><strong>While no plan is enforced there is no quota at all.</strong> In the shipped state
/// - the rollout setting off, or the grandfathering step not completed - the entitlement reports
/// <c>Enforced: false</c> and this guard reserves nothing, writes nothing and refuses nothing, so
/// every upload behaves exactly as it did before this issue. That is the same fail-open direction
/// issue #966 chose deliberately: nothing may be withheld from a business while nothing is being
/// enforced for it.</para>
///
/// <para><strong>A refusal is about the document, never about the record.</strong> A purchase or an
/// expense can always be recorded without one (issue #976), so the caller turns a refusal into a
/// refused <em>upload</em> and nothing else.</para>
/// </summary>
public sealed class AttachmentQuotaGuard
{
    private readonly GetEffectiveEntitlement _entitlement;
    private readonly IAttachmentQuotaStore _store;

    public AttachmentQuotaGuard(GetEffectiveEntitlement entitlement, IAttachmentQuotaStore store)
    {
        _entitlement = entitlement;
        _store = store;
    }

    /// <summary>
    /// Reserves room for an upload of <paramref name="uploadBytes"/> bytes.
    /// </summary>
    /// <param name="category">Which kind of attachment is being uploaded.</param>
    /// <param name="recordId">
    /// The purchase or expense it will be recorded on, or <see langword="null"/> when that record
    /// does not exist yet. What the upload costs is computed from the stored row, never from this.
    /// </param>
    /// <param name="uploadBytes">The uploaded file's size.</param>
    /// <param name="cancellationToken">Cancels the reads and the reservation write.</param>
    public async Task<AttachmentQuotaDecision> ReserveAsync(
        DocumentCategory category, int? recordId, long uploadBytes, CancellationToken cancellationToken = default)
    {
        if (await LimitForCurrentBusinessAsync(cancellationToken).ConfigureAwait(false) is not { } limitBytes)
            return AttachmentQuotaDecision.NotEnforced;

        var reservation = await _store
            .ReserveAsync(new AttachmentUploadRequest(category, recordId, uploadBytes, limitBytes), cancellationToken)
            .ConfigureAwait(false);

        return reservation is null
            ? AttachmentQuotaDecision.Full
            : AttachmentQuotaDecision.Reserved(reservation);
    }

    /// <summary>
    /// Releases a reservation whose upload did not end up being recorded - a refused write, a
    /// conflict, or a failure. Safe to call with a decision that reserved nothing, and safe to call
    /// after a commit consumed the reservation.
    /// </summary>
    public async Task ReleaseAsync(AttachmentQuotaDecision decision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);

        if (decision.Reservation is { } reservation)
            await _store.ReleaseAsync(reservation, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The current business's allowance, or <see langword="null"/> when no plan is being enforced
    /// for it. An effective plan this code does not declare is treated as no enforcement rather
    /// than as the nearest plan it knows, for the reason the entitlement policy treats an unreadable
    /// record as one it cannot read: guessing an allowance would withhold storage nobody agreed to
    /// withhold.
    /// </summary>
    private async Task<long?> LimitForCurrentBusinessAsync(CancellationToken cancellationToken) =>
        AttachmentStorageAllowance.For(
            await _entitlement.Handle(cancellationToken).ConfigureAwait(false));
}
