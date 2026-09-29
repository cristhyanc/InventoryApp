using Inventory.Domain.Expenses;

namespace Inventory.Application.Expenses;

/// <summary>The optional filters the list endpoint accepts, all combined with AND.</summary>
public sealed record OperatingExpenseFilter(
    DateTime? From,
    DateTime? To,
    ExpenseCategory? Category,
    int? SupplierId,
    long? SiteId,
    long? MachineId);
