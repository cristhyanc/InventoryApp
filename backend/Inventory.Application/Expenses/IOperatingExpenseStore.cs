using Inventory.Application.Attachments;

namespace Inventory.Application.Expenses;

/// <summary>Narrow persistence port for operating expenses, owned by the Application layer.</summary>
public interface IOperatingExpenseStore
{
    Task<IReadOnlyList<OperatingExpenseListItem>> ListAsync(OperatingExpenseFilter filter, CancellationToken cancellationToken);

    Task<OperatingExpenseRecord?> FindByIdAsync(int id, CancellationToken cancellationToken);

    /// <summary>
    /// Records a new expense, with <paramref name="attachment"/>'s metadata when it has a
    /// supporting document.
    ///
    /// <para><paramref name="attachmentReservation"/> is the storage reservation that document was
    /// written under (issue #968), and <c>null</c> when there is no document or no plan is being
    /// enforced. It is consumed in the same transaction as the insert, so the reserved bytes become
    /// the attachment's own recorded size with the row.</para>
    /// </summary>
    Task<OperatingExpenseRecord> AddAsync(
        OperatingExpenseFields fields,
        OperatingExpenseAttachmentMetadata? attachment,
        AttachmentUploadReservation? attachmentReservation,
        CancellationToken cancellationToken);

    /// <summary>
    /// Updates the expense's fields and, when <paramref name="newAttachment"/> is not <c>null</c>,
    /// its attachment metadata; a <c>null</c> <paramref name="newAttachment"/> leaves whatever
    /// attachment the expense already has untouched. Returns <c>null</c> when no expense with
    /// <paramref name="id"/> exists.
    ///
    /// <para>Recording an attachment also moves the expense's attachment version on, inside the
    /// same transaction and only while it is still at the version
    /// <paramref name="attachmentReservation"/> was computed against (issue #968). Of two
    /// concurrent replacements exactly one commits; the other throws
    /// <see cref="AttachmentReplacementConflictException"/> with the expense untouched. An expired
    /// reservation whose upload no longer fits throws
    /// <see cref="AttachmentStorageQuotaExceededException"/>, again with the expense untouched.</para>
    /// </summary>
    Task<OperatingExpenseRecord?> UpdateAsync(
        int id,
        OperatingExpenseFields fields,
        OperatingExpenseAttachmentMetadata? newAttachment,
        AttachmentUploadReservation? attachmentReservation,
        CancellationToken cancellationToken);

    /// <summary>Deletes the expense and returns the row as it was just before deletion, or <c>null</c> if it did not exist.</summary>
    Task<OperatingExpenseRecord?> DeleteAsync(int id, CancellationToken cancellationToken);
}
