using Inventory.Domain.Expenses;

namespace Inventory.Application.Expenses;

/// <summary>A persisted operating expense as seen by the Application layer.</summary>
public sealed record OperatingExpenseRecord(
    int Id,
    DateTime ExpenseDate,
    ExpenseCategory Category,
    string Description,
    decimal AmountExGst,
    decimal GstAmount,
    decimal TotalAmount,
    int? SupplierId,
    OperatingExpenseSupplier? Supplier,
    long? SiteId,
    long? MachineId,
    string? AttachmentFileName,
    string? AttachmentStoredFileName,
    string? AttachmentContentType,
    long? AttachmentFileSizeBytes,
    DateTime? ServicePeriodStart,
    DateTime? ServicePeriodEnd,
    string? Notes,
    DateTime CreatedAt,
    DateTime UpdatedAt);
