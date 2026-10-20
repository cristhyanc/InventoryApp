using Inventory.Domain.Expenses;

namespace Inventory.Application.Expenses;

/// <summary>
/// The optional filters the list endpoint accepts, all combined with AND. <c>MachineId</c> is the
/// local machine id the expenses record since issue #780, so it matches the same value create and
/// update accept; a machine the business does not have simply matches no expense.
/// </summary>
public sealed record OperatingExpenseFilter(
    DateTime? From,
    DateTime? To,
    ExpenseCategory? Category,
    int? SupplierId,
    long? SiteId,
    long? MachineId);
