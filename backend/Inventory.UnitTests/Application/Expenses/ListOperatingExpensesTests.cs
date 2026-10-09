using Inventory.Application.Expenses;
using Inventory.Domain.Expenses;
using Xunit;

namespace InventoryApi.Tests.Application.Expenses;

public class ListOperatingExpensesTests
{
    private static OperatingExpenseFields Fields(ExpenseCategory category, DateTime expenseDate) => new(
        expenseDate, category, "Expense", 10m, 1m, 11m, null, null, null, null, null, null);

    [Fact]
    public async Task Applies_the_category_filter_and_orders_newest_first()
    {
        var store = new FakeOperatingExpenseStore();
        var documents = new FakeDocumentStorage();
        var create = new CreateOperatingExpense(store, documents);
        await create.Handle(Fields(ExpenseCategory.Insurance, new DateTime(2026, 1, 1)), null, CancellationToken.None);
        await create.Handle(Fields(ExpenseCategory.Software, new DateTime(2026, 2, 1)), null, CancellationToken.None);
        await create.Handle(Fields(ExpenseCategory.Insurance, new DateTime(2026, 3, 1)), null, CancellationToken.None);

        var filter = new OperatingExpenseFilter(null, null, ExpenseCategory.Insurance, null, null, null);
        var result = await new ListOperatingExpenses(store).Handle(filter, CancellationToken.None);

        Assert.Equal(
            new[] { new DateTime(2026, 3, 1), new DateTime(2026, 1, 1) },
            result.Select(x => x.ExpenseDate));
    }
}
