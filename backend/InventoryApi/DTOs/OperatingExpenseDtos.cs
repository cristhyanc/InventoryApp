using InventoryApi.Models;

namespace InventoryApi.DTOs;

public record OperatingExpenseDto(
    DateTime ExpenseDate,
    OperatingExpenseCategory Category,
    string Description,
    decimal AmountExGst,
    decimal GstAmount,
    decimal TotalAmount,
    int? SupplierId = null,
    long? SiteId = null,
    long? MachineId = null,
    DateTime? ServicePeriodStart = null,
    DateTime? ServicePeriodEnd = null,
    string? Notes = null);

public record OperatingExpenseReportRowDto(
    int Id,
    DateTime ExpenseDate,
    OperatingExpenseCategory Category,
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

public record OperatingExpenseReportDto(
    DateTime From,
    DateTime To,
    IReadOnlyList<OperatingExpenseReportRowDto> Rows,
    decimal TotalExGst,
    decimal TotalGst,
    decimal TotalAmount);

/// <summary>
/// The wire shape of a single operating expense, replacing the EF <c>OperatingExpense</c> entity
/// the controller used to serialize directly. Field names, order, and the embedded
/// <see cref="SupplierResponse"/> shape match that entity's own serializable surface exactly, so
/// this is not a contract change.
/// </summary>
public record OperatingExpenseResponse(
    int Id,
    DateTime ExpenseDate,
    OperatingExpenseCategory Category,
    string Description,
    decimal AmountExGst,
    decimal GstAmount,
    decimal TotalAmount,
    int? SupplierId,
    SupplierResponse? Supplier,
    long? SiteId,
    long? MachineId,
    string? AttachmentFileName,
    string? AttachmentContentType,
    long? AttachmentFileSizeBytes,
    DateTime? ServicePeriodStart,
    DateTime? ServicePeriodEnd,
    string? Notes,
    DateTime CreatedAt,
    DateTime UpdatedAt);
