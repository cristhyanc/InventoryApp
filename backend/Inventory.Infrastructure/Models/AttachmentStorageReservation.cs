using System.Text.Json.Serialization;
using Inventory.Application.Documents;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// Room one upload has reserved in its business's attachment storage allowance (issue #968).
///
/// <para><strong>Why a row and not a counter.</strong> A document's bytes live outside the
/// database, so writing the file and recording the row that refers to it cannot be one
/// transaction. The reservation is what makes the gap safe: the bytes are charged to the business
/// <em>before</em> anything is written, so two concurrent uploads cannot both pass a check that
/// only one of them fits, and a crash between the write and the row leaves a reservation that
/// expires rather than storage that is quietly gone. On success the reservation becomes the
/// attachment's own recorded size, in the same transaction as the row - so the two never both
/// count, and never both stop counting.</para>
///
/// <para><strong>Tenant-owned, with nothing added.</strong> A reservation is a fact about one
/// business, so the central mechanism gives it the business key, the index, the query filter and
/// the ownership stamp; no adapter writes a <c>BusinessId</c> of its own.</para>
///
/// <para><strong>It is not an audit record.</strong> Nothing here names a file, a supplier, an
/// amount or a person: an upload in progress is accounting for bytes, and a row that outlived its
/// upload is deleted rather than kept.</para>
/// </summary>
public class AttachmentStorageReservation : IBusinessOwned
{
    /// <summary>The row's own key, assigned by the database.</summary>
    public int Id { get; set; }

    /// <inheritdoc />
    [JsonIgnore]
    public int BusinessId { get; set; }

    /// <summary>
    /// Which kind of attachment the upload is, which is also which table <see cref="RecordId"/>
    /// names. Stored as the declared <see cref="DocumentCategory"/> value.
    /// </summary>
    public required DocumentCategory Category { get; set; }

    /// <summary>
    /// The purchase or operating expense the attachment will be recorded on, or <c>null</c> when
    /// that record does not exist yet - a purchase or expense being created with a document. It is
    /// deliberately not a foreign key: the record it names may never come into existence, and a
    /// reservation for one that is deleted meanwhile must expire rather than block the delete.
    /// </summary>
    public int? RecordId { get; set; }

    /// <summary>
    /// The uploaded file's full size. Kept beside <see cref="ChargeableBytes"/> because an expired
    /// reservation is re-validated at commit from the upload's size and the attachment's
    /// <em>current</em> size, not from the figure that was charged half an hour earlier.
    /// </summary>
    public long UploadBytes { get; set; }

    /// <summary>
    /// What this upload was charged: the full size for a new attachment, or the net increase over
    /// the attachment being replaced (<c>Inventory.Domain.Attachments.AttachmentStorageQuotaPolicy</c>).
    /// This is the figure that counts against the business while the reservation is unexpired.
    /// </summary>
    public long ChargeableBytes { get; set; }

    /// <summary>
    /// The allowance the reservation was checked against, so a commit can re-validate an expired
    /// reservation without reading the plan again inside the write transaction.
    /// </summary>
    public long LimitBytes { get; set; }

    /// <summary>
    /// The record's <c>AttachmentVersion</c> when the chargeable figure was computed, or <c>null</c>
    /// when the record does not exist yet or carries no version. The commit points the row at the
    /// new file only while it is still at this version, so of two replacements of one attachment
    /// exactly one wins and neither takes credit twice for the same old file.
    /// </summary>
    public int? RecordVersion { get; set; }

    /// <summary>When the reservation was taken, from the application clock.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// When it stops counting, <strong>exclusive</strong>: at this instant it is expired. Expiry is
    /// read rather than swept, so an abandoned upload releases the business's storage on its own.
    /// </summary>
    public DateTime ExpiresAtUtc { get; set; }
}
