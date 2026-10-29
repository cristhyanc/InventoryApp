using Inventory.Application.Documents;
using Inventory.Domain.Attachments;

namespace Inventory.Application.Attachments;

/// <summary>
/// What one business's attachments currently occupy (issue #968), split into the two things it is
/// made of so neither can be mistaken for the other.
/// </summary>
/// <param name="StoredBytes">
/// The recorded sizes of the attachments its active purchase and expense rows refer to. An
/// attachment whose size was never recorded contributes nothing - it is reported by the
/// reconciliation command rather than guessed at - and an orphan file nothing refers to is not
/// charged to the business at all.
/// </param>
/// <param name="ReservedBytes">
/// The chargeable bytes of its unexpired upload reservations: storage that is spoken for by an
/// upload in progress. An expired reservation contributes nothing without anything having to sweep
/// it.
/// </param>
public sealed record AttachmentStorageUsage(long StoredBytes, long ReservedBytes)
{
    /// <summary>What the quota is measured against: both halves together.</summary>
    public long TotalBytes => StoredBytes + ReservedBytes;
}

/// <summary>
/// An upload asking for room before any bytes are written (issue #968).
/// </summary>
/// <param name="Category">
/// Which kind of attachment, which is also which table <paramref name="RecordId"/> is in.
/// </param>
/// <param name="RecordId">
/// The purchase or operating expense the attachment will be recorded on, or <see langword="null"/>
/// when the record does not exist yet (a purchase or expense being created with a document). Never
/// a business: the business is the one the request resolved.
/// </param>
/// <param name="UploadBytes">The uploaded file's size, already validated against the per-file limit.</param>
/// <param name="LimitBytes">The business's plan allowance, from the plan catalogue.</param>
public sealed record AttachmentUploadRequest(
    DocumentCategory Category, int? RecordId, long UploadBytes, long LimitBytes);

/// <summary>
/// Room that has been reserved for one upload (issue #968), and the token the write transaction
/// consumes it with.
///
/// <para>It carries everything the commit needs to decide again on its own: the figure that was
/// charged, the upload's full size and the allowance it was charged against - so an expired
/// reservation can be re-validated inside the write transaction without trusting anything the
/// request says - and the version the attachment was at when the reservation was computed, so two
/// replacements of one attachment cannot both take credit for the same old file.</para>
/// </summary>
/// <param name="Id">The reservation row's key, in the business that holds it.</param>
/// <param name="Category">Which kind of attachment was reserved for.</param>
/// <param name="RecordId">The record it is for, or <see langword="null"/> for one not created yet.</param>
/// <param name="UploadBytes">The uploaded file's size.</param>
/// <param name="ChargeableBytes">
/// What it was charged: the full size for a new attachment, the net increase for a replacement
/// (<see cref="AttachmentStorageQuotaPolicy.ChargeableBytes"/>).
/// </param>
/// <param name="LimitBytes">The allowance it was checked against.</param>
/// <param name="RecordVersion">
/// The record's attachment version when the reservation was computed, or <see langword="null"/>
/// when the record does not exist yet or does not carry one. The commit writes only while the
/// record is still at this version.
/// </param>
/// <param name="ExpiresAtUtc">When it stops counting against the business.</param>
public sealed record AttachmentUploadReservation(
    int Id,
    DocumentCategory Category,
    int? RecordId,
    long UploadBytes,
    long ChargeableBytes,
    long LimitBytes,
    int? RecordVersion,
    DateTime ExpiresAtUtc);

/// <summary>
/// What a use case got when it asked for room (issue #968): nothing to do because no plan is being
/// enforced, a reservation to commit with, or a refusal.
/// </summary>
/// <param name="Refused">
/// Whether the upload was refused for storage. The record itself is never refused with it: a
/// purchase or expense can always be saved without a document.
/// </param>
/// <param name="Reservation">
/// The reservation to hand to the write, or <see langword="null"/> both when the upload was refused
/// and when no plan is being enforced - in which case no reservation exists and the write behaves
/// exactly as it did before quotas.
/// </param>
public sealed record AttachmentQuotaDecision(bool Refused, AttachmentUploadReservation? Reservation)
{
    /// <summary>No plan is enforced, so nothing is reserved and nothing is refused.</summary>
    public static AttachmentQuotaDecision NotEnforced { get; } = new(false, null);

    /// <summary>The upload does not fit.</summary>
    public static AttachmentQuotaDecision Full { get; } = new(true, null);

    /// <summary>Room was reserved.</summary>
    public static AttachmentQuotaDecision Reserved(AttachmentUploadReservation reservation) =>
        new(false, reservation);
}

/// <summary>
/// The business's attachment storage as <c>GET /api/me/access</c> reports it (issue #968).
/// </summary>
/// <param name="UsedBytes">Usage as <see cref="AttachmentStorageUsage.TotalBytes"/> defines it.</param>
/// <param name="LimitBytes">The effective plan's allowance.</param>
/// <param name="WarningLevel">Which of the three states that puts the business in.</param>
public sealed record AttachmentStorageStatus(
    long UsedBytes, long LimitBytes, AttachmentStorageWarningLevel WarningLevel);

/// <summary>
/// Thrown by a write transaction that found an expired reservation and re-validated the quota
/// inside itself, with the upload no longer fitting (issue #968). The row is left exactly as it
/// was; the use case that catches it removes the file it had written and releases the reservation.
/// </summary>
public sealed class AttachmentStorageQuotaExceededException : InvalidOperationException
{
    public AttachmentStorageQuotaExceededException()
        : base(AttachmentStorageQuotaPolicy.StorageFullMessage)
    {
    }

    public AttachmentStorageQuotaExceededException(string message)
        : base(message)
    {
    }

    public AttachmentStorageQuotaExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown by a write transaction whose record's attachment moved on after the reservation was
/// computed (issue #968): another replacement committed first, so this one must not point the row
/// at its file. Exactly one replacement wins, and the loser retries from the current state.
/// </summary>
public sealed class AttachmentReplacementConflictException : InvalidOperationException
{
    public AttachmentReplacementConflictException()
        : base(AttachmentStorageQuotaPolicy.ReplacementConflictMessage)
    {
    }

    public AttachmentReplacementConflictException(string message)
        : base(message)
    {
    }

    public AttachmentReplacementConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
