namespace InventoryApi.DTOs;

/// <summary>
/// The expense category the operating-expense endpoints bind and serialise (issue #305), replacing
/// the <c>Inventory.Infrastructure.Models.OperatingExpenseCategory</c> persistence enum these DTOs used to
/// carry, so the HTTP boundary no longer names the persistence model at all.
///
/// It mirrors that enum - and <see cref="Inventory.Domain.Expenses.ExpenseCategory"/>, which
/// mirrors it too - member for member and value for value, the same convention
/// <c>Inventory.Domain.SupplierOrders.SupplierOrderStatus</c> already followed for its own
/// persistence enum. The numeric values are what clients send and read and what the
/// <c>OperatingExpenses.Category</c> column stores, so they are fixed: the three enums convert by a
/// plain cast, the published <c>OperatingExpenseCategory</c> schema is unchanged, and
/// <c>InventoryApi.Tests.DTOs.OperatingExpenseJsonContractTests</c> asserts the three stay in step.
/// </summary>
public enum OperatingExpenseCategory
{
    NayaxMonthlyFee,
    Insurance,
    RepairsAndMaintenance,
    Software,
    Accounting,
    PhoneInternet,
    VehicleTravel,
    BankFees,
    Other
}

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
