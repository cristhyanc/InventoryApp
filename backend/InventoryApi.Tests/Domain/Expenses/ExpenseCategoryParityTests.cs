using Inventory.Domain.Expenses;
using InventoryApi.Models;
using Xunit;

namespace InventoryApi.Tests.Domain.Expenses;

/// <summary>
/// <see cref="ExpenseCategory"/> and <see cref="OperatingExpenseCategory"/> convert by a plain
/// cast at the InventoryApi boundary (<c>OperatingExpensesController</c>), so they must stay
/// identical member-for-member and ordinal-for-ordinal. Adding a member to one enum without the
/// other would otherwise silently miscategorise expenses instead of failing the build.
/// </summary>
public class ExpenseCategoryParityTests
{
    [Fact]
    public void The_two_category_enums_have_the_same_members_with_the_same_ordinals()
    {
        var domain = Enum.GetValues<ExpenseCategory>()
            .Select(value => (Name: value.ToString(), Ordinal: (int)value))
            .ToList();
        var api = Enum.GetValues<OperatingExpenseCategory>()
            .Select(value => (Name: value.ToString(), Ordinal: (int)value))
            .ToList();

        Assert.Equal(api, domain);
    }
}
