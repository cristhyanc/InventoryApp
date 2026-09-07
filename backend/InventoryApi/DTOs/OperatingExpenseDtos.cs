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
    int? ReceiptId = null,
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
    int? ReceiptId,
    string? ReceiptFileName,
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
