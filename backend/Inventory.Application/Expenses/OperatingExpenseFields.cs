using Inventory.Domain.Expenses;

namespace Inventory.Application.Expenses;

/// <summary>
/// The editable fields of an operating expense, shared by create and update. Attachment metadata
/// is deliberately separate (see <see cref="OperatingExpenseAttachmentMetadata"/>) because it is
/// optional and independently replaceable.
/// </summary>
public sealed record OperatingExpenseFields(
    DateTime ExpenseDate,
    ExpenseCategory Category,
    string Description,
    decimal AmountExGst,
    decimal GstAmount,
    decimal TotalAmount,
    int? SupplierId,
    long? SiteId,
    long? MachineId,
    DateTime? ServicePeriodStart,
    DateTime? ServicePeriodEnd,
    string? Notes);
