using Inventory.Domain.Expenses;

namespace Inventory.Application.Expenses;

/// <summary>
/// One row of the operating-expense list/report, carrying the supplier's name rather than the
/// full <see cref="OperatingExpenseSupplier"/> since that is all the existing list contract needs.
/// </summary>
public sealed record OperatingExpenseListItem(
    int Id,
    DateTime ExpenseDate,
    ExpenseCategory Category,
    string Description,
    decimal AmountExGst,
    decimal GstAmount,
    decimal TotalAmount,
    int? SupplierId,
    string? SupplierName,
    long? SiteId,
    long? MachineId,
    string? AttachmentFileName,
    long? AttachmentFileSizeBytes,
    DateTime? ServicePeriodStart,
    DateTime? ServicePeriodEnd,
    string? Notes);
