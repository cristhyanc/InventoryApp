namespace Inventory.Application.Expenses;

/// <summary>Narrow persistence port for operating expenses, owned by the Application layer.</summary>
public interface IOperatingExpenseStore
{
    Task<IReadOnlyList<OperatingExpenseListItem>> ListAsync(OperatingExpenseFilter filter, CancellationToken cancellationToken);

    Task<OperatingExpenseRecord?> FindByIdAsync(int id, CancellationToken cancellationToken);

    Task<OperatingExpenseRecord> AddAsync(OperatingExpenseFields fields, OperatingExpenseAttachmentMetadata? attachment, CancellationToken cancellationToken);

    /// <summary>
    /// Updates the expense's fields and, when <paramref name="newAttachment"/> is not <c>null</c>,
    /// its attachment metadata; a <c>null</c> <paramref name="newAttachment"/> leaves whatever
    /// attachment the expense already has untouched. Returns <c>null</c> when no expense with
    /// <paramref name="id"/> exists.
    /// </summary>
    Task<OperatingExpenseRecord?> UpdateAsync(int id, OperatingExpenseFields fields, OperatingExpenseAttachmentMetadata? newAttachment, CancellationToken cancellationToken);

    /// <summary>Deletes the expense and returns the row as it was just before deletion, or <c>null</c> if it did not exist.</summary>
    Task<OperatingExpenseRecord?> DeleteAsync(int id, CancellationToken cancellationToken);
}
