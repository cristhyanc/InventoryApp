namespace Inventory.Domain.Expenses;

/// <summary>
/// The categories of operating expense the business tracks. Mirrors
/// <c>InventoryApi.Models.OperatingExpenseCategory</c> member-for-member (same names, same
/// ordinal values), since Domain cannot reference that InventoryApi enum directly; the two
/// convert by a plain cast at the InventoryApi boundary.
/// </summary>
public enum ExpenseCategory
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
